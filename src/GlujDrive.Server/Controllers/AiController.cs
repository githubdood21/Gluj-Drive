using GlujDrive.Application.Semantic;
using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/ai")]
public sealed class AiController(ISemanticSearchService semanticSearch) : ControllerBase
{
    [HttpGet("status")]
    [HostOnly]
    [ProducesResponseType<SemanticStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SemanticStatus>> GetStatusAsync(
        CancellationToken cancellationToken) =>
        Ok(await semanticSearch.GetStatusAsync(cancellationToken));

    [HttpGet("devices")]
    [HostOnly]
    [ProducesResponseType<IReadOnlyList<SemanticDevice>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SemanticDevice>>> GetDevicesAsync(
        CancellationToken cancellationToken) =>
        Ok(await semanticSearch.GetDevicesAsync(cancellationToken));

    [HttpPost("model/download")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DownloadModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await semanticSearch.StartModelDownloadAsync(cancellationToken);
            return Accepted();
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message));
        }
    }

    [HttpPut("settings")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetSettingsAsync(
        SemanticSettingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await semanticSearch.SetComputeSelectionAsync(
                request.ComputeSelection,
                cancellationToken);
            return NoContent();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message));
        }
    }

    [HttpPost("analysis")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AnalyzeAsync(
        SemanticAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await semanticSearch.StartAnalysisAsync(request.ReanalyzeAll, cancellationToken);
            return Accepted();
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message));
        }
    }

    [HttpPost("analysis/cancel")]
    [HostOnly]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CancelAnalysisAsync(CancellationToken cancellationToken)
    {
        await semanticSearch.CancelAnalysisAsync(cancellationToken);
        return Accepted();
    }

    private static ProblemDetails Problem(string detail) => new()
    {
        Title = "The semantic search operation could not be started.",
        Detail = detail
    };
}

public sealed record SemanticSettingsRequest(string ComputeSelection);

public sealed record SemanticAnalysisRequest(bool ReanalyzeAll);
