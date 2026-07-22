using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlujDrive.Application.Storage;

namespace GlujDrive.Infrastructure.Storage;

public sealed class LocalAssetStorage : IAssetStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _catalogPath;
    private readonly string _registryPath;
    private List<SourceFolder> _folders;
    private IReadOnlyDictionary<Guid, LocatedAsset> _assetIndex =
        new Dictionary<Guid, LocatedAsset>();

    public LocalAssetStorage(string catalogPath, string defaultFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFolderPath);

        var fullCatalogPath = NormalizeFolderPath(catalogPath);
        var fullDefaultFolderPath = Path.GetFullPath(defaultFolderPath);

        if (PathsOverlap(fullCatalogPath, fullDefaultFolderPath))
        {
            throw new InvalidOperationException(
                "The catalog and default source folder must be separate directories.");
        }

        Directory.CreateDirectory(fullCatalogPath);
        _catalogPath = fullCatalogPath;
        _registryPath = Path.Combine(fullCatalogPath, "folders.json");
        _folders = LoadFolders();

        if (_folders.Count == 0)
        {
            Directory.CreateDirectory(fullDefaultFolderPath);
            _folders.Add(new SourceFolder(
                Guid.NewGuid(),
                GetDefaultFolderName(fullDefaultFolderPath),
                fullDefaultFolderPath,
                IsDefault: true,
                IsAvailable: true,
                DateTimeOffset.UtcNow));
            PersistFolders();
        }
        else if (_folders.All(folder => !folder.IsDefault))
        {
            _folders[0] = _folders[0] with { IsDefault = true };
            PersistFolders();
        }
    }

    public async Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var lockTaken = false;

        try
        {
            try
            {
                await _gate.WaitAsync(cancellationToken);
                lockTaken = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return [];
            }

            return _folders
                .Select(folder => folder with { IsAvailable = Directory.Exists(folder.Path) })
                .OrderByDescending(folder => folder.IsDefault)
                .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (lockTaken)
            {
                _gate.Release();
            }
        }
    }

    public async Task<SourceFolder> AddFolderAsync(
        AddSourceFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);

        var fullPath = NormalizeFolderPath(request.Path);

        if (PathsOverlap(_catalogPath, fullPath))
        {
            throw new InvalidOperationException(
                "A source folder cannot overlap the application catalog directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The folder '{fullPath}' does not exist or is not currently available.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_folders.Any(folder => PathsOverlap(folder.Path, fullPath)))
            {
                throw new InvalidOperationException(
                    "The folder is already registered or overlaps another registered folder.");
            }

            if (request.MakeDefault)
            {
                _folders = _folders
                    .Select(folder => folder with { IsDefault = false })
                    .ToList();
            }

            var folder = new SourceFolder(
                Guid.NewGuid(),
                NormalizeFolderName(request.Name, fullPath),
                fullPath,
                request.MakeDefault || _folders.Count == 0,
                IsAvailable: true,
                DateTimeOffset.UtcNow);

            _folders.Add(folder);
            await PersistFoldersAsync(cancellationToken);
            _assetIndex = BuildAssetIndex(_folders, cancellationToken);
            return folder;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var index = _folders.FindIndex(folder => folder.Id == folderId);

            if (index < 0)
            {
                return false;
            }

            var removedDefault = _folders[index].IsDefault;
            _folders.RemoveAt(index);

            if (removedDefault && _folders.Count > 0)
            {
                _folders[0] = _folders[0] with { IsDefault = true };
            }

            await PersistFoldersAsync(cancellationToken);
            _assetIndex = BuildAssetIndex(_folders, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetDefaultFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var folder = _folders.SingleOrDefault(candidate => candidate.Id == folderId);

            if (folder is null)
            {
                return false;
            }

            if (!Directory.Exists(folder.Path))
            {
                throw new DirectoryNotFoundException(
                    $"The folder '{folder.Path}' is not currently available.");
            }

            _folders = _folders
                .Select(candidate => candidate with { IsDefault = candidate.Id == folderId })
                .ToList();
            await PersistFoldersAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AssetFile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var lockTaken = false;

        try
        {
            try
            {
                await _gate.WaitAsync(cancellationToken);
                lockTaken = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return [];
            }

            _assetIndex = BuildAssetIndex(_folders, cancellationToken);
            return _assetIndex.Values
                .Select(location => location.Asset)
                .OrderBy(asset => asset.FolderName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(asset => asset.ModifiedAtUtc)
                .ThenBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            if (lockTaken)
            {
                _gate.Release();
            }
        }
    }

    public async Task<AssetFile> StoreAsync(
        StoreAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Length <= 0)
        {
            throw new ArgumentException("The uploaded file is empty.", nameof(request));
        }

        var fileName = NormalizeClientFileName(request.FileName);

        if (!SupportedMediaTypes.TryGetContentType(fileName, out var contentType))
        {
            throw new ArgumentException("The media format is not supported.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var folder = request.FolderId is null
                ? _folders.SingleOrDefault(candidate => candidate.IsDefault)
                : _folders.SingleOrDefault(candidate => candidate.Id == request.FolderId);

            if (folder is null)
            {
                throw new InvalidOperationException(
                    request.FolderId is null
                        ? "No default upload folder is configured."
                        : "The selected upload folder is not registered.");
            }

            if (!Directory.Exists(folder.Path))
            {
                throw new DirectoryNotFoundException(
                    $"The folder '{folder.Path}' is not currently available.");
            }

            var destinationFolderPath = ResolveUploadFolderPath(
                folder.Path,
                request.RelativeFolderPath);
            var destinationPath = GetUniqueDestinationPath(destinationFolderPath, fileName);
            var temporaryPath = Path.Combine(
                destinationFolderPath,
                $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.uploading");

            try
            {
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await request.Content.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, destinationPath);
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }

            var locatedAsset = CreateLocatedAsset(folder, destinationPath, contentType);
            var updatedIndex = new Dictionary<Guid, LocatedAsset>(_assetIndex)
            {
                [locatedAsset.Asset.Id] = locatedAsset
            };
            _assetIndex = updatedIndex;
            return locatedAsset.Asset;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AssetFile?> GetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        (await FindAssetAsync(assetId, cancellationToken))?.Asset;

    public async Task<AssetReadResult?> OpenReadAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var location = await FindAssetAsync(assetId, cancellationToken);

        if (location is null || !File.Exists(location.FullPath))
        {
            return null;
        }

        var content = new FileStream(
            location.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new AssetReadResult(location.Asset, content);
    }

    public async Task<bool> MoveToTrashAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!_assetIndex.TryGetValue(assetId, out var location))
            {
                _assetIndex = BuildAssetIndex(_folders, cancellationToken);

                if (!_assetIndex.TryGetValue(assetId, out location))
                {
                    return false;
                }
            }

            if (!File.Exists(location.FullPath))
            {
                return false;
            }

            var folder = _folders.SingleOrDefault(candidate => candidate.Id == location.Asset.FolderId);

            if (folder is null)
            {
                return false;
            }

            var trashPath = Path.Combine(folder.Path, ".gluj-trash");
            Directory.CreateDirectory(trashPath);
            var destinationPath = GetUniqueDestinationPath(
                trashPath,
                location.Asset.FileName);
            File.Move(location.FullPath, destinationPath);

            var updatedIndex = new Dictionary<Guid, LocatedAsset>(_assetIndex);
            updatedIndex.Remove(assetId);
            _assetIndex = updatedIndex;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int?> EmptyFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var folder = _folders.SingleOrDefault(candidate => candidate.Id == folderId);

            if (folder is null)
            {
                return null;
            }

            if (!Directory.Exists(folder.Path))
            {
                throw new DirectoryNotFoundException("The source folder is not currently available.");
            }

            _assetIndex = BuildAssetIndex(_folders, cancellationToken);
            var assetsToMove = _assetIndex.Values
                .Where(location => location.Asset.FolderId == folderId)
                .ToArray();

            if (assetsToMove.Length == 0)
            {
                return 0;
            }

            var trashPath = Path.Combine(folder.Path, ".gluj-trash");
            Directory.CreateDirectory(trashPath);

            var movedCount = 0;

            foreach (var location in assetsToMove)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(location.FullPath))
                {
                    continue;
                }

                var destinationPath = GetUniqueDestinationPath(trashPath, location.Asset.FileName);
                File.Move(location.FullPath, destinationPath);
                movedCount++;
            }

            _assetIndex = BuildAssetIndex(_folders, cancellationToken);
            return movedCount;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocatedAsset?> FindAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (_assetIndex.TryGetValue(assetId, out var location))
        {
            return location;
        }

        await ListAsync(cancellationToken);
        return _assetIndex.GetValueOrDefault(assetId);
    }

    private static IReadOnlyDictionary<Guid, LocatedAsset> BuildAssetIndex(
        IEnumerable<SourceFolder> folders,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<Guid, LocatedAsset>();
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(folder.Path))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(folder.Path, "*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(folder.Path, filePath);

                if (ContainsTrashDirectory(relativePath) ||
                    !SupportedMediaTypes.TryGetContentType(filePath, out var contentType))
                {
                    continue;
                }

                try
                {
                    var location = CreateLocatedAsset(folder, filePath, contentType);
                    index[location.Asset.Id] = location;
                }
                catch (FileNotFoundException)
                {
                    // A file may be moved while a scan is in progress.
                }
            }
        }

        return index;
    }

    private static LocatedAsset CreateLocatedAsset(
        SourceFolder folder,
        string fullPath,
        string contentType)
    {
        var fileInfo = new FileInfo(fullPath);
        var relativePath = Path.GetRelativePath(folder.Path, fileInfo.FullName);
        var assetId = CreateAssetId(folder.Id, relativePath);
        SupportedMediaTypes.TryGet(fileInfo.Name, out var mediaType);
        var asset = new AssetFile(
            assetId,
            folder.Id,
            folder.Name,
            relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            fileInfo.Name,
            contentType,
            mediaType.Kind,
            fileInfo.Length,
            new DateTimeOffset(fileInfo.CreationTimeUtc),
            new DateTimeOffset(fileInfo.LastWriteTimeUtc));

        return new LocatedAsset(asset, fileInfo.FullName);
    }

    private static Guid CreateAssetId(Guid folderId, string relativePath)
    {
        var identity = folderId.ToString("N") + "\0" +
            relativePath.Replace('\\', '/').ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(hash.AsSpan(0, 16));
    }

    private List<SourceFolder> LoadFolders()
    {
        if (!File.Exists(_registryPath))
        {
            return [];
        }

        try
        {
            var folders = JsonSerializer.Deserialize<List<SourceFolder>>(
                File.ReadAllText(_registryPath),
                JsonOptions) ?? [];

            return folders
                .Select(folder => folder with
                {
                    Path = NormalizeFolderPath(folder.Path),
                    IsAvailable = Directory.Exists(folder.Path)
                })
                .ToList();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The source-folder registry is invalid.", exception);
        }
    }

    private void PersistFolders()
    {
        var temporaryPath = _registryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_folders, JsonOptions));
        File.Move(temporaryPath, _registryPath, overwrite: true);
    }

    private async Task PersistFoldersAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _registryPath + ".tmp";

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                _folders,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, _registryPath, overwrite: true);
    }

    private static string NormalizeClientFileName(string fileName)
    {
        var leafName = Path.GetFileName(fileName);
        var normalized = new string(leafName.Where(character => !char.IsControl(character)).ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A valid file name is required.", nameof(fileName));
        }

        return normalized.Length <= 255 ? normalized : normalized[..255];
    }

    private static string NormalizeFolderName(string? name, string path)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? GetDefaultFolderName(path) : name.Trim();
        var normalized = new string(candidate.Where(character => !char.IsControl(character)).ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A valid folder name is required.", nameof(name));
        }

        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string GetDefaultFolderName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string NormalizeFolderPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ResolveUploadFolderPath(string sourceFolderPath, string? relativeFolderPath)
    {
        if (string.IsNullOrWhiteSpace(relativeFolderPath))
        {
            return sourceFolderPath;
        }

        if (Path.IsPathRooted(relativeFolderPath))
        {
            throw new ArgumentException("The upload subfolder must be relative to its source folder.");
        }

        var normalizedRelativePath = relativeFolderPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);
        var destinationPath = NormalizeFolderPath(Path.Combine(sourceFolderPath, normalizedRelativePath));

        if (!IsSameOrDescendant(destinationPath, sourceFolderPath) ||
            ContainsTrashDirectory(Path.GetRelativePath(sourceFolderPath, destinationPath)))
        {
            throw new ArgumentException("The upload subfolder is outside the registered source folder.");
        }

        if (!Directory.Exists(destinationPath))
        {
            throw new DirectoryNotFoundException("The selected upload subfolder no longer exists.");
        }

        return destinationPath;
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var normalizedCandidate = NormalizeFolderPath(candidate);
        var normalizedParent = NormalizeFolderPath(parent);

        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTrashDirectory(string relativePath) =>
        relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(part => part.Equals(".gluj-trash", StringComparison.OrdinalIgnoreCase));

    private static string GetUniqueDestinationPath(string folderPath, string fileName)
    {
        var initialPath = Path.Combine(folderPath, fileName);

        if (!File.Exists(initialPath))
        {
            return initialPath;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(folderPath, $"{name} ({suffix}){extension}");

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not create a unique file name for '{fileName}'.");
    }

    private sealed record LocatedAsset(AssetFile Asset, string FullPath);
}
