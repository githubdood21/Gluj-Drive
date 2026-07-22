using System.Text;
using GlujDrive.Application.Semantic;
using GlujDrive.Application.Storage;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(
    IAssetStorage assetStorage,
    IAssetVisualService visualService,
    ISemanticSearchService semanticSearch) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SearchResponse>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new ProblemDetails { Title = "Enter a search query." });
        }

        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        if (!TryDecodeCursor(request.Cursor, out var offset))
        {
            return BadRequest(new ProblemDetails { Title = "The search cursor is invalid." });
        }

        var assets = await assetStorage.ListAsync(cancellationToken);

        try
        {
            var ranked = await semanticSearch.SearchAsync(
                request.Query,
                assets,
                1000,
                cancellationToken);
            var assetById = assets.ToDictionary(asset => asset.Id);
            var page = ranked.Matches.Skip(offset).Take(pageSize).ToArray();
            var responses = new List<AssetResponse>(page.Length);

            foreach (var match in page)
            {
                if (assetById.TryGetValue(match.AssetId, out var asset))
                {
                    responses.Add(await AssetResponse.FromAssetAsync(
                        asset,
                        visualService,
                        cancellationToken));
                }
            }

            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < ranked.Matches.Count
                ? EncodeCursor(nextOffset)
                : null;
            return Ok(new SearchResponse(
                responses,
                nextCursor,
                ranked.Matches.Count,
                ranked.SemanticParticipated,
                ranked.Indexed,
                ranked.Eligible));
        }
        catch (InvalidOperationException)
        {
            // A missing/incompatible runtime must never take filename search down.
            var lexical = assets
                .Where(asset =>
                    asset.FileName.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                    asset.FolderName.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                    asset.RelativePath.Contains(request.Query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(asset => asset.ModifiedAtUtc)
                .ToArray();
            var page = lexical.Skip(offset).Take(pageSize).ToArray();
            var responses = new List<AssetResponse>(page.Length);

            foreach (var asset in page)
            {
                responses.Add(await AssetResponse.FromAssetAsync(asset, visualService, cancellationToken));
            }

            return Ok(new SearchResponse(
                responses,
                offset + page.Length < lexical.Length ? EncodeCursor(offset + page.Length) : null,
                lexical.Length,
                false,
                0,
                assets.Count));
        }
    }

    internal static string EncodeCursor(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));

    internal static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrEmpty(cursor))
        {
            return true;
        }

        try
        {
            return int.TryParse(
                       Encoding.UTF8.GetString(Convert.FromBase64String(cursor)),
                       out offset) &&
                   offset >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record SearchRequest(string Query, int PageSize = 60, string? Cursor = null);

public sealed record SearchResponse(
    IReadOnlyList<AssetResponse> Items,
    string? NextCursor,
    int Total,
    bool SemanticParticipated,
    int Indexed,
    int Eligible);
