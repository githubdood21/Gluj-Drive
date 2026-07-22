using GlujDrive.Application.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpSize = SixLabors.ImageSharp.Size;

namespace GlujDrive.Infrastructure.Storage;

public sealed class CachedAssetVisualService : IAssetVisualService
{
    private const string FallbackColor = "#34303A";
    private readonly IAssetStorage _assetStorage;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _processingSlots = new(2, 2);

    public CachedAssetVisualService(string catalogPath, IAssetStorage assetStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(assetStorage);

        _assetStorage = assetStorage;
        _cachePath = Path.Combine(Path.GetFullPath(catalogPath), "previews");
        Directory.CreateDirectory(_cachePath);
    }

    public async Task<AssetVisualMetadata> GetMetadataAsync(
        AssetFile asset,
        CancellationToken cancellationToken = default)
    {
        var colorPath = GetCacheFilePath(asset, "color", "txt");

        if (File.Exists(colorPath))
        {
            return new AssetVisualMetadata(await File.ReadAllTextAsync(colorPath, CancellationToken.None));
        }

        return new AssetVisualMetadata(FallbackColor);
    }

    public async Task<AssetPreview?> OpenPreviewAsync(
        AssetFile asset,
        AssetPreviewSize size,
        CancellationToken cancellationToken = default)
    {
        var cachePath = GetCacheFilePath(asset, size.ToString().ToLowerInvariant(), "webp");
        var unsupportedMarkerPath = cachePath + ".unsupported";

        if (File.Exists(unsupportedMarkerPath))
        {
            return null;
        }

        if (!File.Exists(cachePath))
        {
            try
            {
                await _processingSlots.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                if (!File.Exists(cachePath) &&
                    !await GeneratePreviewAsync(asset, cachePath, size, cancellationToken))
                {
                    await WriteTextAtomicallyAsync(
                        unsupportedMarkerPath,
                        "This image format could not be decoded by the preview pipeline.",
                        cancellationToken);
                    return null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                _processingSlots.Release();
            }
        }

        var content = new FileStream(
            cachePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new AssetPreview(content, "image/webp");
    }

    private async Task<bool> GeneratePreviewAsync(
        AssetFile asset,
        string destinationPath,
        AssetPreviewSize size,
        CancellationToken cancellationToken)
    {
        var source = await _assetStorage.OpenReadAsync(asset.Id, cancellationToken);

        if (source is null)
        {
            return false;
        }

        await using var content = source.Content;

        try
        {
            var maximumDimension = size == AssetPreviewSize.Low ? 64 : 640;
            using var image = await SharpImage.LoadAsync<Rgba32>(
                new DecoderOptions
                {
                    TargetSize = new SharpSize(maximumDimension, maximumDimension),
                    MaxFrames = 1
                },
                content,
                cancellationToken);
            image.Mutate(context => context
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Size = new SharpSize(maximumDimension, maximumDimension),
                    Mode = ResizeMode.Max,
                    Sampler = size == AssetPreviewSize.Low
                        ? KnownResamplers.Bicubic
                        : KnownResamplers.Lanczos3
                }));

            var colorPath = GetCacheFilePath(asset, "color", "txt");

            if (!File.Exists(colorPath))
            {
                using var colorSample = image.Clone(context => context.Resize(new ResizeOptions
                {
                    Size = new SharpSize(1, 1),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Box
                }));
                var pixel = default(Rgba32);
                colorSample.ProcessPixelRows(accessor => pixel = accessor.GetRowSpan(0)[0]);
                var alpha = pixel.A / 255d;
                var red = (byte)Math.Round(pixel.R * alpha + 52 * (1 - alpha));
                var green = (byte)Math.Round(pixel.G * alpha + 48 * (1 - alpha));
                var blue = (byte)Math.Round(pixel.B * alpha + 58 * (1 - alpha));
                await WriteTextAtomicallyAsync(
                    colorPath,
                    $"#{red:X2}{green:X2}{blue:X2}",
                    cancellationToken);
            }

            var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";

            try
            {
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous))
                {
                    await image.SaveAsync(
                        output,
                        new WebpEncoder
                        {
                            Quality = size == AssetPreviewSize.Low ? 38 : 76
                        },
                        cancellationToken);
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return true;
        }
        catch (Exception exception) when (IsUnsupportedImage(exception))
        {
            return false;
        }
    }

    private string GetCacheFilePath(AssetFile asset, string kind, string extension)
    {
        var version = $"{asset.ModifiedAtUtc.UtcTicks:X}-{asset.Length:X}";
        return Path.Combine(_cachePath, $"{asset.Id:N}-{version}-{kind}.{extension}");
    }

    private static async Task WriteTextAtomicallyAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsUnsupportedImage(Exception exception) =>
        exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException;
}
