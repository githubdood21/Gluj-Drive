namespace GlujDrive.Application.Semantic;

public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";

    public bool Enabled { get; set; } = true;

    public string DataPath { get; set; } = "data/catalog/semantic";

    public string RuntimeLibraryPath { get; set; } = "GlujDrive.Inference.Native.dll";

    public string? ModelPackageUrl { get; set; }

    public string? ModelPackageSha256 { get; set; }

    public int IdleUnloadMinutes { get; set; } = 5;
}
