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
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0 && request.FolderId is null && request.MediaKinds is null)
        {
            return BadRequest(new ProblemDetails { Title = "Enter a search query or select a filter." });
        }

        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        if (!TryDecodeCursor(request.Cursor, out var offset))
        {
            return BadRequest(new ProblemDetails { Title = "The search cursor is invalid." });
        }

        var assets = await assetStorage.ListAsync(cancellationToken);
        if (!TryFilterAssets(request, assets, out var filteredAssets, out var filterError))
        {
            return BadRequest(new ProblemDetails { Title = filterError });
        }

        if (query.Length == 0)
        {
            var filtered = filteredAssets
                .OrderByDescending(asset => asset.ModifiedAtUtc)
                .ToArray();
            var filteredPage = filtered.Skip(offset).Take(pageSize).ToArray();
            var filteredResponses = new List<AssetResponse>(filteredPage.Length);
            foreach (var asset in filteredPage)
            {
                filteredResponses.Add(await AssetResponse.FromAssetAsync(asset, visualService, cancellationToken));
            }

            return Ok(new SearchResponse(
                filteredResponses,
                offset + filteredPage.Length < filtered.Length
                    ? EncodeCursor(offset + filteredPage.Length)
                    : null,
                filtered.Length,
                false,
                0,
                filtered.Length));
        }

        try
        {
            var ranked = await semanticSearch.SearchAsync(
                query,
                filteredAssets,
                1000,
                cancellationToken);
            var assetById = filteredAssets.ToDictionary(asset => asset.Id);
            var page = ranked.Matches.Skip(offset).Take(pageSize).ToArray();
            var responses = new List<AssetResponse>(page.Length);

            foreach (var match in page)
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
            var lexical = filteredAssets
                .Where(asset =>
                    asset.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    asset.FolderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    asset.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
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
                filteredAssets.Count));
        }
    }

    private static bool TryFilterAssets(
        SearchRequest request,
        IReadOnlyList<AssetFile> assets,
        out IReadOnlyList<AssetFile> filtered,
        out string? error)
    {
        error = null;
        var query = assets.AsEnumerable();

        if (request.FolderId is not null)
        {
            query = query.Where(asset => asset.FolderId == request.FolderId);
            var scope = (request.RelativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            if (scope.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            {
                filtered = [];
                error = "The folder search scope is invalid.";
                return false;
            }

            if (scope.Length > 0)
            {
                query = query.Where(asset => asset.RelativePath.StartsWith(
                    scope + "/",
                    StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.RelativePath))
        {
            filtered = [];
            error = "Select a source folder before selecting a subfolder scope.";
            return false;
        }

        if (request.MediaKinds is not null)
        {
            var kinds = new HashSet<AssetMediaKind>();
            foreach (var value in request.MediaKinds)
            {
                var parsed = value.Equals("gif", StringComparison.OrdinalIgnoreCase)
                    ? AssetMediaKind.Animation
                    : Enum.TryParse<AssetMediaKind>(value, true, out var kind)
                        ? kind
                        : (AssetMediaKind?)null;
                if (parsed is null)
                {
                    filtered = [];
                    error = $"Unknown media type '{value}'. Use image, gif/animation, or video.";
                    return false;
                }
                kinds.Add(parsed.Value);
            }
            query = query.Where(asset => kinds.Contains(asset.MediaKind));
        }

        filtered = query.ToArray();
        return true;
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

public sealed record SearchRequest(
    string? Query,
    int PageSize = 60,
    string? Cursor = null,
    Guid? FolderId = null,
    string? RelativePath = null,
    IReadOnlyList<string>? MediaKinds = null);

public sealed record SearchResponse(
    IReadOnlyList<AssetResponse> Items,
    string? NextCursor,
    int Total,
    bool SemanticParticipated,
    int Indexed,
    int Eligible);
