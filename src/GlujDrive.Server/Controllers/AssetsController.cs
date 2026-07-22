using GlujDrive.Application.Storage;
using GlujDrive.Application.Semantic;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/assets")]
public sealed class AssetsController(
    IAssetStorage assetStorage,
    IAssetVisualService visualService,
    ISemanticSearchService semanticSearch) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AssetResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AssetResponse>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var assets = await assetStorage.ListAsync(cancellationToken);
        var responses = new List<AssetResponse>(assets.Count);

        foreach (var asset in assets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Ok(Array.Empty<AssetResponse>());
            }

            responses.Add(await AssetResponse.FromAssetAsync(
                asset,
                visualService,
                CancellationToken.None));
        }

        return Ok(responses);
    }

    [HttpGet("{assetId:guid}/preview")]
    [Produces("image/webp")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewAsync(
        Guid assetId,
        [FromQuery] AssetPreviewSize size = AssetPreviewSize.Medium,
        CancellationToken cancellationToken = default)
    {
        var asset = await assetStorage.GetAsync(assetId, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        var preview = await visualService.OpenPreviewAsync(asset, size, cancellationToken);

        if (preview is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=2592000, immutable";
        return File(preview.Content, preview.ContentType, enableRangeProcessing: false);
    }

    [HttpGet("{assetId:guid}")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ViewAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await assetStorage.OpenReadAsync(assetId, cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        return File(
            result.Content,
            result.Asset.ContentType,
            enableRangeProcessing: true);
    }

    [HttpGet("{assetId:guid}/download")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await assetStorage.OpenReadAsync(assetId, cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        return File(
            result.Content,
            result.Asset.ContentType,
            result.Asset.FileName,
            enableRangeProcessing: true);
    }

    [HttpGet("{assetId:guid}/similar")]
    [ProducesResponseType<SimilarAssetsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimilarAssetsResponse>> SimilarAsync(
        Guid assetId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var assets = await assetStorage.ListAsync(cancellationToken);
        var ranked = await semanticSearch.FindSimilarAsync(
            assetId,
            assets,
            Math.Clamp(limit, 1, 1000),
            cancellationToken);
        var assetById = assets.ToDictionary(asset => asset.Id);
        var responses = new List<AssetResponse>(ranked.Matches.Count);

        foreach (var match in ranked.Matches)
        {
            if (assetById.TryGetValue(match.AssetId, out var asset))
            {
                var response = await AssetResponse.FromAssetAsync(
                    asset,
                    visualService,
                    cancellationToken);
                responses.Add(response with { MatchConfidence = match.Confidence });
            }
        }

        return Ok(new SimilarAssetsResponse(
            responses,
            ranked.SemanticParticipated,
            ranked.Indexed,
            ranked.Eligible));
    }

    [HttpDelete("{assetId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var deleted = await assetStorage.MoveToTrashAsync(assetId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}

public sealed record SimilarAssetsResponse(
    IReadOnlyList<AssetResponse> Items,
    bool SemanticParticipated,
    int Indexed,
    int Eligible);
