using System.Text.Json.Serialization;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed record SemanticModelManifest(
    string ModelId,
    string Version,
    string Fingerprint,
    int EmbeddingDimensions,
    int ImageWidth,
    int ImageHeight,
    int ContextLength,
    int StartTokenId,
    int EndTokenId,
    string VocabularyFile,
    string MergesFile,
    IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "manifest.json";

    [JsonIgnore]
    public string DisplayVersion => $"{ModelId} {Version}";
}

internal sealed record SemanticEmbeddingRecord(
    ulong VectorKey,
    Guid AssetId,
    Guid FolderId,
    string RelativePath,
    long Length,
    long ModifiedUtcTicks,
    string ModelFingerprint,
    float[]? Embedding,
    DateTimeOffset? AnalyzedAtUtc,
    string? Failure);
