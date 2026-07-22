namespace GlujDrive.Application.Storage;

public interface IAssetVisualService
{
    Task<AssetVisualMetadata> GetMetadataAsync(
        AssetFile asset,
        CancellationToken cancellationToken = default);

    Task<AssetPreview?> OpenPreviewAsync(
        AssetFile asset,
        AssetPreviewSize size,
        CancellationToken cancellationToken = default);
}
