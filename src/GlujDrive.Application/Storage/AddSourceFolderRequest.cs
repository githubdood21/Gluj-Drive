namespace GlujDrive.Application.Storage;

public sealed record AddSourceFolderRequest(
    string Path,
    string? Name,
    bool MakeDefault);
