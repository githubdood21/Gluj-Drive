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
    private readonly IAssetVisualService _visualService;
    private readonly SemanticSearchOptions _options;
    private readonly SemanticCatalog _catalog;
    private readonly SemanticModelPackage _modelPackage;
    private readonly NativeTinyClipInference _inference;
    private readonly SemanticVectorIndex _vectorIndex;
    private readonly ILogger<SemanticSearchService> _logger;
    private CancellationTokenSource? _analysisCancellation;
    private SemanticJobStatus _job = EmptyJob("idle");
    private string _installState = "idle";
    private string _installPhase = "Not installed";
    private double _installProgress;
    private string? _installError;
    private int _lastEligible;
    private DateTimeOffset _lastEligibleRefreshUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    private SemanticSearchService(
        IAssetStorage assetStorage,
        IAssetVisualService visualService,
        SemanticSearchOptions options,
        SemanticCatalog catalog,
        SemanticModelPackage modelPackage,
        NativeTinyClipInference inference,
        SemanticVectorIndex vectorIndex,
        ILogger<SemanticSearchService> logger)
    {
        _assetStorage = assetStorage;
        _visualService = visualService;
        _options = options;
        _catalog = catalog;
        _modelPackage = modelPackage;
        _inference = inference;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    public static SemanticSearchService Create(
        IAssetStorage assetStorage,
        IAssetVisualService visualService,
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
            visualService,
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
        var runtimeAvailable = _inference.RuntimeAvailable;
        var compute = await GetComputeSelectionAsync(cancellationToken);
        var records = await _catalog.ListAsync(cancellationToken);
        SemanticJobStatus job;
        string installState;
        string installPhase;
        double installProgress;
        string? installError;

        lock (_stateGate)
        {
            job = _job;
            installState = _installState;
            installPhase = _installPhase;
            installProgress = _installProgress;
            installError = _installError;
        }

        if (installState == "idle" && manifest is not null && runtimeAvailable)
        {
            installState = "installed";
            installPhase = "Ready";
            installProgress = 100;
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
            runtimeAvailable,
            manifest is not null,
            _modelPackage.CanInstall,
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
            installState,
            installPhase,
            installProgress,
            installError,
            job);
    }

    public Task<IReadOnlyList<SemanticDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        _inference.GetDevicesAsync(cancellationToken);

    public Task StartInstallationAsync(CancellationToken cancellationToken = default)
    {
        if (!_modelPackage.CanInstall)
        {
            throw new InvalidOperationException(
                "This build has no bundled AI package and no release download is configured.");
        }

        lock (_stateGate)
        {
            if (_installState == "installing")
            {
                throw new InvalidOperationException("AI search is already being installed.");
            }

            _installState = "installing";
            _installPhase = "Queued";
            _installProgress = 0;
            _installError = null;
        }

        _ = Task.Run(InstallAiAsync, CancellationToken.None);
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
        var recordByKey = records.ToDictionary(record => record.VectorKey);
        var allowedAssetIds = assets.Select(asset => asset.Id).ToHashSet();
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
                var directVector = await _inference.EmbedTextAsync(
                    normalizedQuery,
                    manifest,
                    compute,
                    cancellationToken);
                var photoVector = await _inference.EmbedTextAsync(
                    $"a photo of {normalizedQuery}",
                    manifest,
                    compute,
                    cancellationToken);
                var queryVector = AverageNormalized(directVector, photoVector);
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

        var scores = new Dictionary<Guid, double>();
        var confidenceByAsset = new Dictionary<Guid, double>();

        for (var rank = 0; rank < lexical.Count; rank++)
        {
            scores[lexical[rank].Id] = 1d / (RrfConstant + rank + 1);
        }

        var eligibleSemantic = semantic
            .Where(match => recordByKey.TryGetValue(match.Key, out var record) &&
                            allowedAssetIds.Contains(record.AssetId))
            .ToArray();
        var bestSimilarity = eligibleSemantic.FirstOrDefault().Similarity;
        var similarityFloor = Math.Max(
            Math.Clamp(_options.MinimumTextSimilarity, -1d, 1d),
            bestSimilarity - Math.Clamp(_options.MaximumTextSimilarityDrop, 0d, 2d));
        var acceptedSemantic = eligibleSemantic
            .Where(match => match.Similarity >= similarityFloor)
            .Take(Math.Clamp(_options.MaximumSemanticCandidates, 1, limit))
            .ToArray();

        for (var rank = 0; rank < acceptedSemantic.Length; rank++)
        {
            var match = acceptedSemantic[rank];
            var record = recordByKey[match.Key];

            scores[record.AssetId] = scores.GetValueOrDefault(record.AssetId) +
                                     1d / (RrfConstant + rank + 1);
            confidenceByAsset[record.AssetId] = ToConfidence(match.Similarity);
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
            .Select(pair => new
            {
                pair.Key,
                Score = pair.Value,
                Confidence = confidenceByAsset.GetValueOrDefault(pair.Key, -1d),
                IsExact = exact?.Id == pair.Key
            })
            .OrderByDescending(match => match.IsExact)
            .ThenByDescending(match => match.Confidence)
            .ThenByDescending(match => match.Score)
            .ThenBy(match => match.Key)
            .Take(limit)
            .Select(match => new SemanticMatch(
                match.Key,
                match.Score,
                match.Confidence >= 0d ? match.Confidence : null))
            .ToArray();
        var indexed = manifest is null ? 0 : records.Count(record =>
            allowedAssetIds.Contains(record.AssetId) &&
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
            .Select(match => new SemanticMatch(
                recordByKey[match.Key].AssetId,
                match.Similarity,
                ToConfidence(match.Similarity)))
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

    private async Task InstallAiAsync()
    {
        try
        {
            var progress = new Progress<SemanticInstallProgress>(value =>
            {
                lock (_stateGate)
                {
                    _installPhase = value.Phase;
                    _installProgress = value.Percent;
                }
            });
            await _modelPackage.InstallAsync(progress, CancellationToken.None);
            await _inference.ResetAsync();

            if (!_inference.RuntimeAvailable)
            {
                throw new InvalidOperationException(
                    "The package was installed, but the Windows native runtime could not be loaded.");
            }

            lock (_stateGate)
            {
                _installState = "installed";
                _installPhase = "Ready";
                _installProgress = 100;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "AI search installation failed.");
            lock (_stateGate)
            {
                _installState = "failed";
                _installPhase = "Installation failed";
                _installError = exception.Message;
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
                    AssetPreview? framePreview = null;
                    AssetReadResult? readResult = null;
                    if (asset.MediaKind is AssetMediaKind.Animation or AssetMediaKind.Video)
                    {
                        framePreview = await _visualService.OpenPreviewAsync(
                            asset,
                            AssetPreviewSize.Medium,
                            cancellationToken) ??
                            throw new InvalidDataException("The first media frame could not be decoded.");
                    }
                    else
                    {
                        readResult = await _assetStorage.OpenReadAsync(asset.Id, cancellationToken) ??
                            throw new FileNotFoundException("The image disappeared during analysis.");
                    }

                    await using var inferenceContent = framePreview?.Content ?? readResult!.Content;
                    {
                        var embedding = await _inference.EmbedImageAsync(
                            inferenceContent,
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

    private static double ToConfidence(float cosineSimilarity) =>
        Math.Clamp(cosineSimilarity, 0f, 1f);

    private static float[] AverageNormalized(params float[][] vectors)
    {
        if (vectors.Length == 0 || vectors.Any(vector => vector.Length != vectors[0].Length))
        {
            throw new ArgumentException("Semantic vectors must have matching dimensions.", nameof(vectors));
        }

        var result = new float[vectors[0].Length];
        foreach (var vector in vectors)
        {
            for (var index = 0; index < result.Length; index++)
            {
                result[index] += vector[index];
            }
        }

        var length = Math.Sqrt(result.Sum(value => value * value));
        if (length <= double.Epsilon)
        {
            throw new InvalidDataException("The combined text embedding was empty.");
        }

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (float)(result[index] / length);
        }
        return result;
    }

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
