using GlujDrive.Application.Storage;
using System.Windows.Forms;

namespace GlujDrive.Infrastructure.Storage;

public sealed class WindowsFolderPicker : IFolderPicker, IDisposable
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public bool IsAvailable => OperatingSystem.IsWindows() && Environment.UserInteractive;

    public async Task<string?> PickFolderAsync(
        string? initialPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "The native folder picker is available only while Gluj Drive runs interactively on Windows.");
        }

        await _dialogGate.WaitAsync(cancellationToken);

        try
        {
            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => ShowDialog(initialPath, completion))
            {
                IsBackground = true,
                Name = "Gluj Drive folder picker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public void Dispose()
    {
        _dialogGate.Dispose();
    }

    private static void ShowDialog(
        string? initialPath,
        TaskCompletionSource<string?> completion)
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose a folder for Gluj Drive to scan",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                dialog.InitialDirectory = initialPath;
            }

            var result = dialog.ShowDialog();
            completion.TrySetResult(
                result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                    ? Path.GetFullPath(dialog.SelectedPath)
                    : null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
