namespace GlujDrive.Application.Semantic;

public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";

    public bool Enabled { get; set; } = true;

    public string DataPath { get; set; } = "data/catalog/semantic";

    public string RuntimeLibraryPath { get; set; } =
        "runtime/win-x64/GlujDrive.Inference.Native.dll";

    public string BundledPackagePath { get; set; } = "ai/TinyCLIP-ncnn-win-x64.zip";

    public string BundledPackageSha256Path { get; set; } =
        "ai/TinyCLIP-ncnn-win-x64.zip.sha256";

    public string? ModelPackageUrl { get; set; }

    public string? ModelPackageSha256 { get; set; }

    public int IdleUnloadMinutes { get; set; } = 5;
}
