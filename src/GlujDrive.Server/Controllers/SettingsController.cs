using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/settings")]
[HostOnly]
public sealed class SettingsController(ServerSettingsStore settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<ServerSettings> Get() => Ok(settings.Current);

    [HttpPut]
    public async Task<ActionResult<ServerSettingsUpdateResponse>> UpdateAsync(
        ServerSettings request,
        CancellationToken cancellationToken)
    {
        try
        {
            var previous = settings.Current;
            var updated = await settings.UpdateAsync(request, cancellationToken);
            var restartRequired = updated.MaxUploadBytes > previous.MaxUploadBytes ||
                                  updated.MaxBatchUploadBytes > previous.MaxBatchUploadBytes;
            return Ok(new ServerSettingsUpdateResponse(updated, restartRequired));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "The server settings are invalid.", Detail = exception.Message });
        }
    }
}

public sealed record ServerSettingsUpdateResponse(ServerSettings Settings, bool RestartRequired);
