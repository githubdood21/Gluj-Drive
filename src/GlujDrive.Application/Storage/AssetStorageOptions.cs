namespace GlujDrive.Application.Storage;

public sealed class AssetStorageOptions
{
    public const string SectionName = "Storage";
    public const long DefaultMaxUploadBytes = 100 * 1024 * 1024;
    public const long DefaultMaxBatchUploadBytes = 500 * 1024 * 1024;

    public string CatalogPath { get; set; } = "data/catalog";

    public string DefaultFolderPath { get; set; } = "data/photos";

    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;

    public long MaxBatchUploadBytes { get; set; } = DefaultMaxBatchUploadBytes;
}
