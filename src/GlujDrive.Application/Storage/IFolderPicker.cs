namespace GlujDrive.Application.Storage;

public interface IFolderPicker
{
    bool IsAvailable { get; }

    Task<string?> PickFolderAsync(
        string? initialPath,
        CancellationToken cancellationToken = default);
}
