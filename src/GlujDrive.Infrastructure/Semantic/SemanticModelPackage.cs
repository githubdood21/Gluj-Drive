using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GlujDrive.Application.Semantic;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed class SemanticModelPackage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly SemanticSearchOptions _options;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SemanticModelManifest? _cachedManifest;

    public SemanticModelPackage(
        HttpClient httpClient,
        SemanticSearchOptions options,
        string dataPath)
    {
        _httpClient = httpClient;
        _options = options;
        _modelPath = Path.Combine(dataPath, "model");
    }

    public bool CanDownload =>
        Uri.TryCreate(_options.ModelPackageUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(_options.ModelPackageSha256);

    public string ModelPath => _modelPath;

    public async Task<SemanticModelManifest?> GetInstalledManifestAsync(
        bool forceValidation = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceValidation && _cachedManifest is not null)
        {
            return _cachedManifest;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!forceValidation && _cachedManifest is not null)
            {
                return _cachedManifest;
            }

            _cachedManifest = await ValidateDirectoryAsync(_modelPath, cancellationToken);
            return _cachedManifest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        if (!CanDownload)
        {
            throw new InvalidOperationException(
                "No semantic model package URL and SHA-256 have been configured.");
        }

        await _gate.WaitAsync(cancellationToken);
        var parentPath = Path.GetDirectoryName(_modelPath)!;
        Directory.CreateDirectory(parentPath);
        var workPath = Path.Combine(parentPath, $"model-download-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(workPath, "model.zip");
        var extractedPath = Path.Combine(workPath, "extracted");

        try
        {
            Directory.CreateDirectory(workPath);
            using var response = await _httpClient.GetAsync(
                _options.ModelPackageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int read;

                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress.Report(totalBytes is > 0 ? copied * 100d / totalBytes.Value : 0d);
                }
            }

            await VerifyFileHashAsync(
                archivePath,
                _options.ModelPackageSha256!,
                cancellationToken);
            Directory.CreateDirectory(extractedPath);
            ExtractSafely(archivePath, extractedPath);

            var packageRoot = ResolvePackageRoot(extractedPath);
            _ = await ValidateDirectoryAsync(packageRoot, cancellationToken) ??
                throw new InvalidDataException("The downloaded model package is invalid.");

            var backupPath = $"{_modelPath}.previous";
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }

            if (Directory.Exists(_modelPath))
            {
                Directory.Move(_modelPath, backupPath);
            }

            try
            {
                Directory.Move(packageRoot, _modelPath);
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, recursive: true);
                }
            }
            catch
            {
                if (Directory.Exists(_modelPath))
                {
                    Directory.Delete(_modelPath, recursive: true);
                }

                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, _modelPath);
                }

                throw;
            }

            _cachedManifest = await ValidateDirectoryAsync(_modelPath, cancellationToken);
            progress.Report(100d);
        }
        finally
        {
            _gate.Release();

            if (Directory.Exists(workPath))
            {
                Directory.Delete(workPath, recursive: true);
            }
        }
    }

    private static async Task<SemanticModelManifest?> ValidateDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(path, SemanticModelManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        SemanticModelManifest? manifest;
        await using (var stream = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<SemanticModelManifest>(
                stream,
                JsonOptions,
                cancellationToken);
        }

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.ModelId) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Fingerprint) ||
            manifest.EmbeddingDimensions <= 0 ||
            manifest.ImageWidth <= 0 ||
            manifest.ImageHeight <= 0 ||
            manifest.ContextLength <= 2 ||
            manifest.StartTokenId < 0 ||
            manifest.EndTokenId < 0 ||
            string.IsNullOrWhiteSpace(manifest.VocabularyFile) ||
            string.IsNullOrWhiteSpace(manifest.MergesFile) ||
            manifest.Files.Count == 0)
        {
            return null;
        }

        foreach (var (relativePath, expectedHash) in manifest.Files)
        {
            var fullPath = ResolveContainedPath(path, relativePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            await VerifyFileHashAsync(fullPath, expectedHash, cancellationToken);
        }

        return manifest;
    }

    private static void ExtractSafely(string archivePath, string destinationPath)
    {
        var destinationRoot = Path.GetFullPath(destinationPath) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(destinationPath, entry.FullName));
            if (!destination.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The model archive contains an unsafe path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static string ResolvePackageRoot(string extractedPath)
    {
        if (File.Exists(Path.Combine(extractedPath, SemanticModelManifest.FileName)))
        {
            return extractedPath;
        }

        var directories = Directory.GetDirectories(extractedPath);
        return directories.Length == 1 &&
               File.Exists(Path.Combine(directories[0], SemanticModelManifest.FileName))
            ? directories[0]
            : extractedPath;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The model manifest contains an unsafe path.");
        }

        return fullPath;
    }

    private static async Task VerifyFileHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        var normalizedExpected = expectedHash.Replace("-", string.Empty, StringComparison.Ordinal).Trim();

        if (!actualHash.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 verification failed for '{Path.GetFileName(path)}'.");
        }
    }
}
