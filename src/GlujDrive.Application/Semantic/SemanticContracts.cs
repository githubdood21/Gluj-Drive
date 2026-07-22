using GlujDrive.Application.Storage;

namespace GlujDrive.Application.Semantic;

public sealed record SemanticDevice(
    string Id,
    string Name,
    string Kind,
    bool IsAvailable);

public sealed record SemanticJobStatus(
    string State,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int Total,
    int Processed,
    int Indexed,
    int Skipped,
    int Failed,
    string? CurrentFile,
    string? Error,
    bool CancellationPending,
    double ImagesPerSecond,
    int? EstimatedSecondsRemaining);

public sealed record SemanticStatus(
    bool Enabled,
    bool RuntimeAvailable,
    bool ModelInstalled,
    bool ModelDownloadAvailable,
    string ModelId,
    string? ModelVersion,
    string ComputeSelection,
    string? ActiveDevice,
    string? FallbackReason,
    int Eligible,
    int Indexed,
    int Stale,
    int Failed,
    int Remaining,
    double CoveragePercent,
    string DownloadState,
    double DownloadProgressPercent,
    string? DownloadError,
    SemanticJobStatus Job);

public sealed record SemanticMatch(Guid AssetId, double Score);

public sealed record SemanticSearchResult(
    IReadOnlyList<SemanticMatch> Matches,
    bool SemanticParticipated,
    int Indexed,
    int Eligible);

public interface ISemanticSearchService
{
    Task<SemanticStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SemanticDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    Task StartModelDownloadAsync(CancellationToken cancellationToken = default);

    Task SetComputeSelectionAsync(
        string selection,
        CancellationToken cancellationToken = default);

    Task StartAnalysisAsync(
        bool reanalyzeAll,
        CancellationToken cancellationToken = default);

    Task CancelAnalysisAsync(CancellationToken cancellationToken = default);

    Task<SemanticSearchResult> SearchAsync(
        string query,
        IReadOnlyList<AssetFile> assets,
        int maximumResults,
        CancellationToken cancellationToken = default);

    Task<SemanticSearchResult> FindSimilarAsync(
        Guid assetId,
        IReadOnlyList<AssetFile> assets,
        int maximumResults,
        CancellationToken cancellationToken = default);
}
