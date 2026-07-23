namespace GlujDrive.Application.Semantic;

public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";

    public bool Enabled { get; set; } = true;

    public string DataPath { get; set; } =
        "%LOCALAPPDATA%/Gluj Drive/data/catalog/semantic";

    public string RuntimeLibraryPath { get; set; } =
        "runtime/win-x64/GlujDrive.Inference.Native.dll";

    public string BundledPackagePath { get; set; } = "ai/TinyCLIP-ncnn-win-x64.zip";

    public string BundledPackageSha256Path { get; set; } =
        "ai/TinyCLIP-ncnn-win-x64.zip.sha256";

    public string? ModelPackageUrl { get; set; }

    public string? ModelPackageSha256 { get; set; }

    public int IdleUnloadMinutes { get; set; } = 5;

    // TinyCLIP cosine similarity is not a probability. Values below this floor
    // are too weak to participate in semantic ranking by default.
    public double MinimumTextSimilarity { get; set; } = 0.22;

    // Also reject candidates that trail the best in-scope result substantially.
    public double MaximumTextSimilarityDrop { get; set; } = 0.04;

    public int MaximumSemanticCandidates { get; set; } = 200;
}
