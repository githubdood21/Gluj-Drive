namespace GlujDrive.Application.Storage;

public sealed record AssetVisualMetadata(
    string AverageColor,
    int? PixelWidth = null,
    int? PixelHeight = null);
