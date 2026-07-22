namespace GlujDrive.Application.Storage;

public static class SupportedImageTypes
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif"
        };

    public static string DisplayExtensions => "jpg, jpeg, png, webp, gif, heic, and heif";

    public static bool TryGetContentType(string fileName, out string contentType) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out contentType!);
}
