namespace GlujDrive.Application.Storage;

public sealed record AssetFile(
    Guid Id,
    Guid FolderId,
    string FolderName,
    string RelativePath,
    string FileName,
    string ContentType,
    AssetMediaKind MediaKind,
    long Length,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc);
