using GlujDrive.Application.Storage;
using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(IFolderPicker folderPicker) : ControllerBase
{
    [HttpGet("capabilities")]
    public ActionResult<SystemCapabilitiesResponse> GetCapabilities()
    {
        var isHostConnection = HostConnection.IsLocal(HttpContext);
        return Ok(new SystemCapabilitiesResponse(
            isHostConnection,
            isHostConnection && folderPicker.IsAvailable));
    }

    [HttpPost("folders/pick")]
    [HostOnly]
    [ProducesResponseType<FolderPickerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FolderPickerResponse>> PickFolderAsync(
        FolderPickerRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await folderPicker.PickFolderAsync(
                request.InitialPath,
                cancellationToken);
            return path is null ? NoContent() : Ok(new FolderPickerResponse(path));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The native folder picker is unavailable.",
                Detail = exception.Message
            });
        }
    }
}

public sealed record SystemCapabilitiesResponse(
    bool IsHostConnection,
    bool NativeFolderPicker);

public sealed record FolderPickerRequest(string? InitialPath);

public sealed record FolderPickerResponse(string Path);
