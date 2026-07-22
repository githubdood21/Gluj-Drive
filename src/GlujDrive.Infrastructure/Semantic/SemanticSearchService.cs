using System.Diagnostics;
using System.Text.Json;
using GlujDrive.Application.Semantic;
using GlujDrive.Application.Storage;
using Microsoft.Extensions.Logging;

namespace GlujDrive.Infrastructure.Semantic;

public sealed class SemanticSearchService : ISemanticSearchService, IDisposable
{
    private const string DefaultModelId = "TinyCLIP-auto-ViT-22M-32-Text-10M-LAION400M";
    private const string ComputeSettingKey = "compute-selection";
    private const string JobSettingKey = "analysis-job";
    private const int RrfConstant = 60;

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _jobPersistGate = new(1, 1);
    private readonly IAssetStorage _assetStorage;
    private readonly SemanticSearchOptions _options;
    private readonly SemanticCatalog _catalog;
    private readonly SemanticModelPackage _modelPackage;
    private readonly NativeTinyClipInference _inference;
    private readonly SemanticVectorIndex _vectorIndex;
    private readonly ILogger<SemanticSearchService> _logger;
    private CancellationTokenSource? _analysisCancellation;
    private SemanticJobStatus _job = EmptyJob("idle");
    private string _downloadState = "idle";
    private double _downloadProgress;
    private string? _downloadError;
    private int _lastEligible;
    private DateTimeOffset _lastEligibleRefreshUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    private SemanticSearchService(
        IAssetStorage assetStorage,
        SemanticSearchOptions options,
        SemanticCatalog catalog,
        SemanticModelPackage modelPackage,
        NativeTinyClipInference inference,
        SemanticVectorIndex vectorIndex,
        ILogger<SemanticSearchService> logger)
    {
        _assetStorage = assetStorage;
        _options = options;
        _catalog = catalog;
        _modelPackage = modelPackage;
        _inference = inference;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    public static SemanticSearchService Create(
        IAssetStorage assetStorage,
        SemanticSearchOptions options,
        HttpClient httpClient,
        string dataPath,
        ILogger<SemanticSearchService> logger)
    {
        var catalog = new SemanticCatalog(Path.Combine(dataPath, "semantic.db"));
        var package = new SemanticModelPackage(httpClient, options, dataPath);
        var inference = new NativeTinyClipInference(options, package);
        var index = new SemanticVectorIndex(dataPath);
        var service = new SemanticSearchService(
            assetStorage,
            options,
            catalog,
            package,
            inference,
            index,
            logger);
        service.RestoreJob();
        return service;
    }

    public async Task<SemanticStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _modelPackage.GetInstalledManifestAsync(cancellationToken: cancellationToken);
        var compute = await GetComputeSelectionAsync(cancellationToken);
        var records = await _catalog.ListAsync(cancellationToken);
        SemanticJobStatus job;
        string downloadState;
        double downloadProgress;
        string? downloadError;

        lock (_stateGate)
        {
            job = _job;
            downloadState = _downloadState;
            downloadProgress = _downloadProgress;
            downloadError = _downloadError;
        }

        if (job.State is not "running" &&
            DateTimeOffset.UtcNow - _lastEligibleRefreshUtc > TimeSpan.FromSeconds(30))
        {
            var assets = await _assetStorage.ListAsync(cancellationToken);
            _lastEligible = assets.Count;
            _lastEligibleRefreshUtc = DateTimeOffset.UtcNow;
        }

        var matching = manifest is null
            ? []
            : records.Where(record => record.ModelFingerprint == manifest.Fingerprint).ToArray();
        var indexed = matching.Count(record => record.Embedding is not null);
        var failed = matching.Count(record => record.Failure is not null);
        var stale = Math.Max(0, records.Count - matching.Length);
        var remaining = Math.Max(0, _lastEligible - indexed - failed);
        var coverage = _lastEligible == 0 ? 0 : indexed * 100d / _lastEligible;

        return new SemanticStatus(
            _options.Enabled,
            _inference.RuntimeAvailable,
            manifest is not null,
            _modelPackage.CanDownload,
            manifest?.ModelId ?? DefaultModelId,
            manifest?.Version,
            compute,
            _inference.ActiveDevice,
            _inference.FallbackReason,
            _lastEligible,
            indexed,
            stale,
            failed,
            remaining,
            coverage,
            downloadState,
            downloadProgress,
            downloadError,
            job);
    }

    public Task<IReadOnlyList<SemanticDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        _inference.GetDevicesAsync(cancellationToken);

    public Task StartModelDownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_modelPackage.CanDownload)
        {
            throw new InvalidOperationException(
                "Configure a semantic model package URL and SHA-256 before downloading.");
        }

        lock (_stateGate)
        {
            if (_downloadState == "downloading")
            {
                throw new InvalidOperationException("The semantic model is already downloading.");
            }

            _downloadState = "downloading";
            _downloadProgress = 0;
            _downloadError = null;
        }

        _ = Task.Run(DownloadModelAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task SetComputeSelectionAsync(
        string selection,
        CancellationToken cancellationToken = default)
    {
        var normalized = selection.Trim().ToLowerInvariant();
        var devices = await GetDevicesAsync(cancellationToken);

        if (normalized != "auto" && !devices.Any(device => device.Id == normalized && device.IsAvailable))
        {
            throw new ArgumentException("Select Auto, CPU, or an available Vulkan device.", nameof(selection));
        }

        await _catalog.SetSettingAsync(ComputeSettingKey, normalized, cancellationToken);
        await _inference.ResetAsync(cancellationToken);
    }

    public async Task StartAnalysisAsync(
        bool reanalyzeAll,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Semantic search is disabled in server configuration.");
        }

        if (await _modelPackage.GetInstalledManifestAsync(cancellationToken: cancellationToken) is null)
        {
            throw new InvalidOperationException("Install the semantic model before analyzing the library.");
        }


        if (!_inference.RuntimeAvailable)
        {
            throw new InvalidOperationException("Install the native TinyCLIP runtime before analyzing the library.");
        }

        lock (_stateGate)
        {
            if (_job.State == "running")
            {
                throw new InvalidOperationException("Library analysis is already running.");
            }

            _analysisCancellation?.Dispose();
            _analysisCancellation = new CancellationTokenSource();
            _job = EmptyJob("running") with { StartedAtUtc = DateTimeOffset.UtcNow };
        }

        _ = Task.Run(
            () => AnalyzeLibraryAsync(reanalyzeAll, _analysisCancellation.Token),
            CancellationToken.None);
    }

    public Task CancelAnalysisAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_job.State != "running" || _analysisCancellation is null)
            {
                return Task.CompletedTask;
            }

            _job = _job with { CancellationPending = true };
            _analysisCancellation.Cancel();
        }

        return Task.CompletedTask;
    }

    public async Task<SemanticSearchResult> SearchAsync(
        string query,
        IReadOnlyList<AssetFile> assets,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        var limit = Math.Clamp(maximumResults, 1, 1000);
        var lexical = RankLexically(normalizedQuery, assets);
        var records = await _catalog.ListAsync(cancellationToken);
        var manifest = await _modelPackage.GetInstalledManifestAsync(cancellationToken: cancellationToken);
        var semanticParticipated = false;
        IReadOnlyList<(ulong Key, float Similarity)> semantic = [];

        if (_options.Enabled && manifest is not null && records.Any(record =>
                record.ModelFingerprint == manifest.Fingerprint && record.Embedding is not null))
        {
            try
            {
                await _vectorIndex.EnsureLoadedAsync(manifest, records, cancellationToken);
                var compute = await GetComputeSelectionAsync(cancellationToken);
                var queryVector = await _inference.EmbedTextAsync(
                    normalizedQuery,
                    manifest,
                    compute,
                    cancellationToken);
                semantic = await _vectorIndex.SearchAsync(queryVector, limit, cancellationToken);
                semanticParticipated = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Semantic query inference was unavailable; using filename search.");
            }
        }

        var recordByKey = records.ToDictionary(record => record.VectorKey);
        var scores = new Dictionary<Guid, double>();

        for (var rank = 0; rank < lexical.Count; rank++)
        {
            scores[lexical[rank].Id] = 1d / (RrfConstant + rank + 1);
        }

        for (var rank = 0; rank < semantic.Count; rank++)
        {
            if (!recordByKey.TryGetValue(semantic[rank].Key, out var record))
            {
                continue;
            }

            scores[record.AssetId] = scores.GetValueOrDefault(record.AssetId) +
                                     1d / (RrfConstant + rank + 1);
        }

        var exact = assets.FirstOrDefault(asset =>
            Path.GetFileNameWithoutExtension(asset.FileName)
                .Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            asset.FileName.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            scores[exact.Id] = double.MaxValue;
        }

        var matches = scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(limit)
            .Select(pair => new SemanticMatch(pair.Key, pair.Value))
            .ToArray();
        var indexed = manifest is null ? 0 : records.Count(record =>
            record.ModelFingerprint == manifest.Fingerprint && record.Embedding is not null);

        return new SemanticSearchResult(matches, semanticParticipated, indexed, assets.Count);
    }

    public async Task<SemanticSearchResult> FindSimilarAsync(
        Guid assetId,
        IReadOnlyList<AssetFile> assets,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        var records = await _catalog.ListAsync(cancellationToken);
        var manifest = await _modelPackage.GetInstalledManifestAsync(cancellationToken: cancellationToken);
        var source = manifest is null ? null : records.SingleOrDefault(record =>
            record.AssetId == assetId &&
            record.ModelFingerprint == manifest.Fingerprint &&
            record.Embedding is not null);

        if (manifest is null || source?.Embedding is null)
        {
            return new SemanticSearchResult([], false, 0, assets.Count);
        }

        await _vectorIndex.EnsureLoadedAsync(manifest, records, cancellationToken);
        var nearest = await _vectorIndex.SearchAsync(
            source.Embedding,
            Math.Clamp(maximumResults + 1, 2, 1001),
            cancellationToken);
        var recordByKey = records.ToDictionary(record => record.VectorKey);
        var matches = nearest
            .Where(match => recordByKey.TryGetValue(match.Key, out var record) && record.AssetId != assetId)
            .Take(Math.Clamp(maximumResults, 1, 1000))
            .Select(match => new SemanticMatch(recordByKey[match.Key].AssetId, match.Similarity))
            .ToArray();
        var indexed = records.Count(record =>
            record.ModelFingerprint == manifest.Fingerprint && record.Embedding is not null);
        return new SemanticSearchResult(matches, true, indexed, assets.Count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _inference.Dispose();
        _vectorIndex.Dispose();
    }

    private async Task DownloadModelAsync()
    {
        try
        {
            var progress = new Progress<double>(value =>
            {
                lock (_stateGate)
                {
                    _downloadProgress = value;
                }
            });
            await _modelPackage.DownloadAsync(progress, CancellationToken.None);
            await _inference.ResetAsync();

            lock (_stateGate)
            {
                _downloadState = "installed";
                _downloadProgress = 100;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The semantic model download failed.");
            lock (_stateGate)
            {
                _downloadState = "failed";
                _downloadError = exception.Message;
            }
        }
    }

    private async Task AnalyzeLibraryAsync(bool reanalyzeAll, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var manifest = await _modelPackage.GetInstalledManifestAsync(
                               cancellationToken: cancellationToken) ??
                           throw new InvalidOperationException("The semantic model is not installed.");
            var compute = await GetComputeSelectionAsync(cancellationToken);
            var assets = await _assetStorage.ListAsync(cancellationToken);
            _lastEligible = assets.Count;
            _lastEligibleRefreshUtc = DateTimeOffset.UtcNow;
            var liveIds = assets.Select(asset => asset.Id).ToHashSet();
            await _catalog.PruneAsync(liveIds, cancellationToken);
            var records = await _catalog.ListAsync(cancellationToken);
            var recordByAsset = records.ToDictionary(record => record.AssetId);
            await _vectorIndex.ResetAsync(deletePersisted: true, cancellationToken);
            await _vectorIndex.EnsureLoadedAsync(manifest, records, cancellationToken);

            UpdateJob(job => job with { Total = assets.Count });
            var lastSaveAt = Stopwatch.StartNew();
            var changedSinceSave = 0;

            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateJob(job => job with { CurrentFile = asset.RelativePath });
                recordByAsset.TryGetValue(asset.Id, out var existing);

                if (!reanalyzeAll && existing is not null &&
                    IsCurrent(existing, asset, manifest.Fingerprint))
                {
                    UpdateProgress(stopwatch, skipped: true, failed: false, indexed: existing.Embedding is not null);
                    continue;
                }

                var candidate = new SemanticEmbeddingRecord(
                    existing?.VectorKey ?? 0,
                    asset.Id,
                    asset.FolderId,
                    asset.RelativePath,
                    asset.Length,
                    asset.ModifiedAtUtc.UtcTicks,
                    manifest.Fingerprint,
                    null,
                    null,
                    null);

                try
                {
                    var readResult = await _assetStorage.OpenReadAsync(asset.Id, cancellationToken) ??
                                     throw new FileNotFoundException("The image disappeared during analysis.");
                    await using (readResult.Content)
                    {
                        var embedding = await _inference.EmbedImageAsync(
                            readResult.Content,
                            manifest,
                            compute,
                            cancellationToken);
                        var saved = await _catalog.UpsertSuccessAsync(
                            candidate with { Embedding = embedding },
                            cancellationToken);
                        await _vectorIndex.UpsertAsync(saved, cancellationToken);
                        recordByAsset[asset.Id] = saved;
                    }

                    changedSinceSave++;
                    UpdateProgress(stopwatch, skipped: false, failed: false, indexed: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not analyze image {AssetId}.", asset.Id);
                    var failed = await _catalog.UpsertFailureAsync(candidate, exception.Message, CancellationToken.None);
                    await _vectorIndex.RemoveAsync(failed.VectorKey, CancellationToken.None);
                    recordByAsset[asset.Id] = failed;
                    UpdateProgress(stopwatch, skipped: false, failed: true, indexed: false);
                }

                if (changedSinceSave >= 100 || lastSaveAt.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    await _vectorIndex.SaveAsync(CancellationToken.None);
                    changedSinceSave = 0;
                    lastSaveAt.Restart();
                }
            }

            await _vectorIndex.SaveAsync(CancellationToken.None);
            UpdateJob(job => job with
            {
                State = "completed",
                CurrentFile = null,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                CancellationPending = false,
                EstimatedSecondsRemaining = 0
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _vectorIndex.SaveAsync(CancellationToken.None);
            UpdateJob(job => job with
            {
                State = "cancelled",
                CurrentFile = null,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                CancellationPending = false
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Semantic library analysis failed.");
            UpdateJob(job => job with
            {
                State = "failed",
                CurrentFile = null,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Error = exception.Message,
                CancellationPending = false
            });
        }
    }

    private void UpdateProgress(
        Stopwatch stopwatch,
        bool skipped,
        bool failed,
        bool indexed)
    {
        UpdateJob(job =>
        {
            var processed = job.Processed + 1;
            var speed = stopwatch.Elapsed.TotalSeconds > 0
                ? processed / stopwatch.Elapsed.TotalSeconds
                : 0;
            int? remaining = speed > 0
                ? checked((int)Math.Ceiling(Math.Max(0, job.Total - processed) / speed))
                : null;
            return job with
            {
                Processed = processed,
                Skipped = job.Skipped + (skipped ? 1 : 0),
                Failed = job.Failed + (failed ? 1 : 0),
                Indexed = job.Indexed + (indexed ? 1 : 0),
                ImagesPerSecond = speed,
                EstimatedSecondsRemaining = remaining
            };
        });
    }

    private void UpdateJob(Func<SemanticJobStatus, SemanticJobStatus> update)
    {
        SemanticJobStatus snapshot;
        lock (_stateGate)
        {
            _job = update(_job);
            snapshot = _job;
        }

        _ = PersistJobAsync(snapshot);
    }

    private void RestoreJob()
    {
        try
        {
            var json = _catalog.GetSettingAsync(JobSettingKey).GetAwaiter().GetResult();
            var restored = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<SemanticJobStatus>(json);
            if (restored is null)
            {
                return;
            }

            _job = restored.State == "running"
                ? restored with
                {
                    State = "interrupted",
                    CurrentFile = null,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    CancellationPending = false,
                    Error = "The previous analysis stopped when the server exited. Start analysis to resume."
                }
                : restored;
            _catalog.SetSettingAsync(JobSettingKey, JsonSerializer.Serialize(_job))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not restore the semantic analysis job state.");
        }
    }

    private async Task PersistJobAsync(SemanticJobStatus job)
    {
        await _jobPersistGate.WaitAsync();
        try
        {
            await _catalog.SetSettingAsync(JobSettingKey, JsonSerializer.Serialize(job));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist semantic analysis progress.");
        }
        finally
        {
            _jobPersistGate.Release();
        }
    }

    private async Task<string> GetComputeSelectionAsync(CancellationToken cancellationToken) =>
        await _catalog.GetSettingAsync(ComputeSettingKey, cancellationToken) ?? "auto";

    private static bool IsCurrent(
        SemanticEmbeddingRecord record,
        AssetFile asset,
        string fingerprint) =>
        record.ModelFingerprint == fingerprint &&
        record.FolderId == asset.FolderId &&
        record.RelativePath == asset.RelativePath &&
        record.Length == asset.Length &&
        record.ModifiedUtcTicks == asset.ModifiedAtUtc.UtcTicks &&
        record.Embedding is not null;

    private static List<AssetFile> RankLexically(
        string query,
        IReadOnlyList<AssetFile> assets) =>
        assets
            .Where(asset =>
                asset.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                asset.FolderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                asset.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset =>
                Path.GetFileNameWithoutExtension(asset.FileName)
                    .Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.FileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.ModifiedAtUtc)
            .ToList();

    private static SemanticJobStatus EmptyJob(string state) => new(
        state,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        false,
        0,
        null);
}
