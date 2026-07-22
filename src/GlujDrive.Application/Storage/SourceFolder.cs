namespace GlujDrive.Application.Storage;

public sealed record SourceFolder(
    Guid Id,
    string Name,
    string Path,
    bool IsDefault,
    bool IsAvailable,
    DateTimeOffset AddedAtUtc);
