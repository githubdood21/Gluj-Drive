using GlujDrive.Application.Storage;
using Microsoft.AspNetCore.Mvc;

namespace GlujDrive.Server.Controllers;

[ApiController]
[Route("api/uploads")]
public sealed class UploadsController(
    IAssetStorage assetStorage,
    IAssetVisualService visualService,
    AssetStorageOptions storageOptions) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<IReadOnlyList<AssetResponse>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<IReadOnlyList<AssetResponse>>> UploadAsync(
        List<IFormFile> files,
        [FromQuery] Guid? folderId,
        [FromQuery] string? relativePath,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Select at least one media file to upload."
            });
        }

        if (files.Any(file => file.Length <= 0))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "One or more uploaded files are empty."
            });
        }

        if (files.Any(file => file.Length > storageOptions.MaxUploadBytes))
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Title = "One or more uploaded files are too large.",
                Detail = $"The maximum size per file is {storageOptions.MaxUploadBytes} bytes."
            });
        }

        long totalLength;

        try
        {
            totalLength = files.Sum(file => file.Length);
        }
        catch (OverflowException)
        {
            totalLength = long.MaxValue;
        }

        if (totalLength > storageOptions.MaxBatchUploadBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Title = "The upload batch is too large.",
                Detail = $"The maximum batch size is {storageOptions.MaxBatchUploadBytes} bytes."
            });
        }

        if (files.Any(file => !SupportedMediaTypes.TryGetContentType(file.FileName, out _)))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "One or more media formats are not supported.",
                Detail = $"Supported extensions: {SupportedMediaTypes.DisplayExtensions}."
            });
        }

        var folders = await assetStorage.ListFoldersAsync(cancellationToken);
        var targetFolder = folderId is null
            ? folders.SingleOrDefault(folder => folder.IsDefault)
            : folders.SingleOrDefault(folder => folder.Id == folderId);

        if (targetFolder is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = folderId is null
                    ? "No default upload folder is configured."
                    : "The selected upload folder is not registered."
            });
        }

        if (!targetFolder.IsAvailable)
        {
            return Conflict(new ProblemDetails
            {
                Title = "The selected upload folder is unavailable.",
                Detail = "Check the source folder on the host computer."
            });
        }

        var uploadedAssets = new List<AssetResponse>(files.Count);

        try
        {
            foreach (var file in files)
            {
                await using var content = file.OpenReadStream();
                SupportedMediaTypes.TryGetContentType(file.FileName, out var contentType);
                var asset = await assetStorage.StoreAsync(
                    new StoreAssetRequest(
                        targetFolder.Id,
                        relativePath,
                        file.FileName,
                        contentType,
                        file.Length,
                        content),
                    cancellationToken);
                uploadedAssets.Add(await AssetResponse.FromAssetAsync(
                    asset,
                    visualService,
                    cancellationToken));
            }

            return Created("/api/assets", uploadedAssets);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The uploaded file is invalid.",
                Detail = exception.Message
            });
        }
        catch (DirectoryNotFoundException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The selected upload subfolder is unavailable.",
                Detail = exception.Message
            });
        }
    }
}

public sealed record AssetResponse(
    Guid Id,
    Guid FolderId,
    string FolderName,
    string RelativePath,
    string FileName,
    string ContentType,
    string MediaKind,
    string FileExtension,
    long Length,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    string AverageColor,
    string ViewUrl,
    string DownloadUrl,
    string LowPreviewUrl,
    string PreviewUrl,
    double? MatchConfidence = null)
{
    public static async Task<AssetResponse> FromAssetAsync(
        AssetFile asset,
        IAssetVisualService visualService,
        CancellationToken cancellationToken)
    {
        var visual = await visualService.GetMetadataAsync(asset, cancellationToken);
        var version = $"{asset.ModifiedAtUtc.UtcTicks:X}-{asset.Length:X}";
        return new AssetResponse(
            asset.Id,
            asset.FolderId,
            asset.FolderName,
            asset.RelativePath,
            asset.FileName,
            asset.ContentType,
            asset.MediaKind.ToString().ToLowerInvariant(),
            Path.GetExtension(asset.FileName).TrimStart('.').ToUpperInvariant(),
            asset.Length,
            asset.CreatedAtUtc,
            asset.ModifiedAtUtc,
            visual.AverageColor,
            $"/api/assets/{asset.Id}",
            $"/api/assets/{asset.Id}/download",
            $"/api/assets/{asset.Id}/preview?size=low&v={version}",
            $"/api/assets/{asset.Id}/preview?size=medium&v={version}");
    }
}
