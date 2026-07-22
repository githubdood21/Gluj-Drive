namespace GlujDrive.Application.Storage;

public sealed record StoreAssetRequest(
    Guid? FolderId,
    string? RelativeFolderPath,
    string FileName,
    string ContentType,
    long Length,
    Stream Content);
