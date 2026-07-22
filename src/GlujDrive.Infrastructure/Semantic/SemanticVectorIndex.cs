using System.Text.Json;
using Cloud.Unum.USearch;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed class SemanticVectorIndex : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _indexPath;
    private readonly string _metadataPath;
    private USearchIndex? _index;
    private string? _fingerprint;
    private int _dimensions;
    private bool _dirty;

    public SemanticVectorIndex(string dataPath)
    {
        _indexPath = Path.Combine(dataPath, "vectors.usearch");
        _metadataPath = Path.Combine(dataPath, "vectors.json");
    }

    public async Task EnsureLoadedAsync(
        SemanticModelManifest manifest,
        IReadOnlyList<SemanticEmbeddingRecord> records,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_index is not null &&
                _fingerprint == manifest.Fingerprint &&
                _dimensions == manifest.EmbeddingDimensions)
            {
                return;
            }

            DisposeIndex();

            if (TryLoadSavedIndex(manifest))
            {
                return;
            }

            _index = CreateIndex(manifest.EmbeddingDimensions);
            _fingerprint = manifest.Fingerprint;
            _dimensions = manifest.EmbeddingDimensions;

            foreach (var record in records.Where(record =>
                         record.Embedding is not null &&
                         record.ModelFingerprint == manifest.Fingerprint))
            {
                _index.Add(record.VectorKey, record.Embedding!);
            }

            _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(
        SemanticEmbeddingRecord record,
        CancellationToken cancellationToken = default)
    {
        if (record.Embedding is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_index is null)
            {
                throw new InvalidOperationException("The semantic vector index is not loaded.");
            }

            if (_index.Contains(record.VectorKey))
            {
                _index.Remove(record.VectorKey);
            }

            _index.Add(record.VectorKey, record.Embedding);
            _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(ulong key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_index is not null && _index.Contains(key))
            {
                _index.Remove(key);
                _dirty = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<(ulong Key, float Similarity)>> SearchAsync(
        float[] vector,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_index is null || _index.Size() == 0)
            {
                return [];
            }

            var count = _index.Search(
                vector,
                Math.Min(maximumResults, checked((int)_index.Size())),
                out var keys,
                out var distances);
            var matches = new (ulong Key, float Similarity)[count];

            for (var index = 0; index < count; index++)
            {
                matches[index] = (keys[index], Math.Clamp(1f - distances[index], -1f, 1f));
            }

            return matches;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_index is null || !_dirty || _fingerprint is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            var temporaryIndexPath = $"{_indexPath}.{Guid.NewGuid():N}.tmp";
            var temporaryMetadataPath = $"{_metadataPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                _index.Save(temporaryIndexPath);
                await File.WriteAllTextAsync(
                    temporaryMetadataPath,
                    JsonSerializer.Serialize(
                        new IndexMetadata(_fingerprint, _dimensions),
                        JsonOptions),
                    cancellationToken);
                File.Move(temporaryIndexPath, _indexPath, overwrite: true);
                File.Move(temporaryMetadataPath, _metadataPath, overwrite: true);
                _dirty = false;
            }
            finally
            {
                File.Delete(temporaryIndexPath);
                File.Delete(temporaryMetadataPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(
        bool deletePersisted = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DisposeIndex();
            if (deletePersisted)
            {
                File.Delete(_indexPath);
                File.Delete(_metadataPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        DisposeIndex();
        _gate.Dispose();
    }

    private bool TryLoadSavedIndex(SemanticModelManifest manifest)
    {
        if (!File.Exists(_indexPath) || !File.Exists(_metadataPath))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<IndexMetadata>(
                File.ReadAllText(_metadataPath),
                JsonOptions);

            if (metadata is null ||
                metadata.Fingerprint != manifest.Fingerprint ||
                metadata.Dimensions != manifest.EmbeddingDimensions)
            {
                return false;
            }

            var loaded = new USearchIndex(_indexPath, view: false);

            if (loaded.Dimensions() != checked((ulong)manifest.EmbeddingDimensions))
            {
                loaded.Dispose();
                return false;
            }

            _index = loaded;
            _fingerprint = manifest.Fingerprint;
            _dimensions = manifest.EmbeddingDimensions;
            _dirty = false;
            return true;
        }
        catch
        {
            DisposeIndex();
            return false;
        }
    }

    private static USearchIndex CreateIndex(int dimensions) => new(
        metricKind: MetricKind.Cos,
        quantization: ScalarKind.Float16,
        dimensions: checked((ulong)dimensions),
        connectivity: 16,
        expansionAdd: 128,
        expansionSearch: 64);

    private void DisposeIndex()
    {
        _index?.Dispose();
        _index = null;
        _fingerprint = null;
        _dimensions = 0;
        _dirty = false;
    }

    private sealed record IndexMetadata(string Fingerprint, int Dimensions);
}
