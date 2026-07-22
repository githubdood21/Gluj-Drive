namespace GlujDrive.Application.Storage;

public interface IAssetStorage
{
    Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(
        CancellationToken cancellationToken = default);

    Task<SourceFolder> AddFolderAsync(
        AddSourceFolderRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<bool> SetDefaultFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetFile>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AssetFile?> GetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<AssetFile> StoreAsync(
        StoreAssetRequest request,
        CancellationToken cancellationToken = default);

    Task<AssetReadResult?> OpenReadAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<bool> MoveToTrashAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<int?> EmptyFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default);
}
