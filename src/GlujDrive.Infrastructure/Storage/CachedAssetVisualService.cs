using GlujDrive.Application.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpSize = SixLabors.ImageSharp.Size;

namespace GlujDrive.Infrastructure.Storage;

public sealed class CachedAssetVisualService : IAssetVisualService
{
    private const string FallbackColor = "#34303A";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAssetStorage _assetStorage;
    private readonly string _cachePath;
    private readonly FfmpegVideoFrameExtractor _videoFrameExtractor;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly SemaphoreSlim _processingSlots = new(2, 2);

    public CachedAssetVisualService(
        string catalogPath,
        IAssetStorage assetStorage,
        string ffmpegPath = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(assetStorage);

        _assetStorage = assetStorage;
        _videoFrameExtractor = new FfmpegVideoFrameExtractor(ffmpegPath);
        _cachePath = Path.Combine(Path.GetFullPath(catalogPath), "previews");
        Directory.CreateDirectory(_cachePath);
    }

    public async Task<AssetVisualMetadata> GetMetadataAsync(
        AssetFile asset,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = GetCacheFilePath(asset, "metadata", "json");
        var colorPath = GetCacheFilePath(asset, "color", "txt");

        if (File.Exists(metadataPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CachedVisualMetadata>(
                    await File.ReadAllTextAsync(metadataPath, CancellationToken.None),
                    JsonOptions);
                if (cached is not null)
                {
                    return new AssetVisualMetadata(
                        cached.AverageColor,
                        cached.PixelWidth,
                        cached.PixelHeight);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                // A derivative metadata file is rebuildable. Fall back to the
                // legacy color cache instead of failing the library listing.
            }
        }

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
                    if (asset.MediaKind != AssetMediaKind.Video)
                    {
                        await WriteTextAtomicallyAsync(
                            unsupportedMarkerPath,
                            "This media format could not be decoded by the preview pipeline.",
                            cancellationToken);
                    }
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

        await UpdateMetadataFromPreviewAsync(asset, cachePath);

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

        await using var originalContent = source.Content;
        Stream? extractedFrame = null;

        try
        {
            var maximumDimension = size == AssetPreviewSize.Low ? 64 : 640;
            if (asset.MediaKind == AssetMediaKind.Video)
            {
                extractedFrame = await _videoFrameExtractor.ExtractFirstFrameAsync(
                    originalContent,
                    maximumDimension,
                    cancellationToken);
                if (extractedFrame is null)
                {
                    return false;
                }
            }

            var content = extractedFrame ?? originalContent;
            var identified = await SharpImage.IdentifyAsync(content, cancellationToken);
            content.Position = 0;
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
            var averageColor = File.Exists(colorPath)
                ? await File.ReadAllTextAsync(colorPath, CancellationToken.None)
                : null;

            if (averageColor is null)
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
                averageColor = $"#{red:X2}{green:X2}{blue:X2}";
                await WriteTextAtomicallyAsync(colorPath, averageColor, cancellationToken);
            }

            var pixelWidth = identified.Width;
            var pixelHeight = identified.Height;
            if ((image.Width > image.Height) != (identified.Width > identified.Height))
            {
                (pixelWidth, pixelHeight) = (pixelHeight, pixelWidth);
            }

            await UpdateMetadataAsync(
                asset,
                averageColor,
                pixelWidth,
                pixelHeight,
                cancellationToken);

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
        finally
        {
            if (extractedFrame is not null)
            {
                await extractedFrame.DisposeAsync();
            }
        }
    }

    private string GetCacheFilePath(AssetFile asset, string kind, string extension)
    {
        var version = $"{asset.ModifiedAtUtc.UtcTicks:X}-{asset.Length:X}";
        return Path.Combine(_cachePath, $"{asset.Id:N}-{version}-{kind}.{extension}");
    }

    private async Task UpdateMetadataFromPreviewAsync(AssetFile asset, string previewPath)
    {
        try
        {
            var identified = await SharpImage.IdentifyAsync(previewPath, CancellationToken.None);
            var colorPath = GetCacheFilePath(asset, "color", "txt");
            var averageColor = File.Exists(colorPath)
                ? await File.ReadAllTextAsync(colorPath, CancellationToken.None)
                : FallbackColor;
            await UpdateMetadataAsync(
                asset,
                averageColor,
                identified.Width,
                identified.Height,
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is UnknownImageFormatException or
                InvalidImageContentException or
                NotSupportedException or
                IOException)
        {
            // Cached previews are disposable. A failed metadata upgrade should
            // not prevent the otherwise valid preview from being returned.
        }
    }

    private async Task UpdateMetadataAsync(
        AssetFile asset,
        string averageColor,
        int pixelWidth,
        int pixelHeight,
        CancellationToken cancellationToken)
    {
        var metadataPath = GetCacheFilePath(asset, "metadata", "json");
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            CachedVisualMetadata? existing = null;
            if (File.Exists(metadataPath))
            {
                try
                {
                    existing = JsonSerializer.Deserialize<CachedVisualMetadata>(
                        await File.ReadAllTextAsync(metadataPath, CancellationToken.None),
                        JsonOptions);
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    // Replace corrupt derivative metadata below.
                }
            }

            if (existing is not null &&
                (long)existing.PixelWidth * existing.PixelHeight >= (long)pixelWidth * pixelHeight)
            {
                averageColor = existing.AverageColor;
                pixelWidth = existing.PixelWidth;
                pixelHeight = existing.PixelHeight;
            }

            await WriteTextAtomicallyAsync(
                metadataPath,
                JsonSerializer.Serialize(
                    new CachedVisualMetadata(averageColor, pixelWidth, pixelHeight),
                    JsonOptions),
                cancellationToken);
        }
        finally
        {
            _metadataGate.Release();
        }
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

    private sealed record CachedVisualMetadata(
        string AverageColor,
        int PixelWidth,
        int PixelHeight);
}
