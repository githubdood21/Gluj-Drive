using GlujDrive.Application.Storage;
using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/folders")]
public sealed class FoldersController(IAssetStorage assetStorage) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FolderResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FolderResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var includeLocalPath = HostConnection.IsLocal(HttpContext);
        var folders = await assetStorage.ListFoldersAsync(cancellationToken);
        return Ok(folders.Select(folder => FolderResponse.FromFolder(folder, includeLocalPath)));
    }

    [HttpPost]
    [HostOnly]
    [ProducesResponseType<SourceFolder>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SourceFolder>> AddAsync(
        AddFolderBody body,
        CancellationToken cancellationToken)
    {
        try
        {
            var folder = await assetStorage.AddFolderAsync(
                new AddSourceFolderRequest(body.Path, body.Name, body.MakeDefault),
                cancellationToken);
            return Created($"/api/folders/{folder.Id}", folder);
        }
        catch (DirectoryNotFoundException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The source folder could not be found.",
                Detail = exception.Message
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The source folder is invalid.",
                Detail = exception.Message
            });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The source folder overlaps an existing folder.",
                Detail = exception.Message
            });
        }
    }

    [HttpPut("{folderId:guid}/default")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetDefaultAsync(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await assetStorage.SetDefaultFolderAsync(folderId, cancellationToken)
                ? NoContent()
                : NotFound();
        }
        catch (DirectoryNotFoundException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The source folder is unavailable.",
                Detail = exception.Message
            });
        }
    }

    [HttpDelete("{folderId:guid}")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        Guid folderId,
        CancellationToken cancellationToken) =>
        await assetStorage.RemoveFolderAsync(folderId, cancellationToken)
            ? NoContent()
            : NotFound();

    [HttpDelete("{folderId:guid}/assets")]
    [HostOnly]
    [ProducesResponseType<EmptyFolderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmptyFolderResponse>> EmptyAsync(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var movedCount = await assetStorage.EmptyFolderAsync(folderId, cancellationToken);
            return movedCount is null
                ? NotFound()
                : Ok(new EmptyFolderResponse(movedCount.Value));
        }
        catch (DirectoryNotFoundException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The source folder is unavailable.",
                Detail = exception.Message
            });
        }
    }
}

public sealed record AddFolderBody(string Path, string? Name, bool MakeDefault);

public sealed record EmptyFolderResponse(int MovedToTrash);

public sealed record FolderResponse(
    Guid Id,
    string Name,
    string? Path,
    bool IsDefault,
    bool IsAvailable,
    DateTimeOffset AddedAtUtc,
    IReadOnlyList<SourceSubfolderResponse> Subfolders)
{
    public static FolderResponse FromFolder(SourceFolder folder, bool includeLocalPath) => new(
        folder.Id,
        folder.Name,
        includeLocalPath ? folder.Path : null,
        folder.IsDefault,
        folder.IsAvailable,
        folder.AddedAtUtc,
        DiscoverSubfolders(folder));

    private static IReadOnlyList<SourceSubfolderResponse> DiscoverSubfolders(SourceFolder folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        return Directory
            .EnumerateDirectories(folder.Path, "*", options)
            .Select(path => System.IO.Path.GetRelativePath(folder.Path, path)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Where(relativePath => !relativePath
                .Split('/')
                .Any(part => part.Equals(".gluj-trash", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
            .Select(relativePath => new SourceSubfolderResponse(
                System.IO.Path.GetFileName(relativePath),
                relativePath))
            .ToArray();
    }
}

public sealed record SourceSubfolderResponse(string Name, string RelativePath);
