namespace GlujDrive.Application.Storage;

public enum AssetMediaKind
{
    Image,
    Animation,
    Video
}

public sealed record SupportedMediaType(string ContentType, AssetMediaKind Kind);

public static class SupportedMediaTypes
{
    private static readonly IReadOnlyDictionary<string, SupportedMediaType> Types =
        new Dictionary<string, SupportedMediaType>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = new("image/jpeg", AssetMediaKind.Image),
            [".jpeg"] = new("image/jpeg", AssetMediaKind.Image),
            [".png"] = new("image/png", AssetMediaKind.Image),
            [".webp"] = new("image/webp", AssetMediaKind.Image),
            [".gif"] = new("image/gif", AssetMediaKind.Animation),
            [".heic"] = new("image/heic", AssetMediaKind.Image),
            [".heif"] = new("image/heif", AssetMediaKind.Image),
            [".mp4"] = new("video/mp4", AssetMediaKind.Video),
            [".m4v"] = new("video/x-m4v", AssetMediaKind.Video),
            [".mov"] = new("video/quicktime", AssetMediaKind.Video),
            [".webm"] = new("video/webm", AssetMediaKind.Video),
            [".ogv"] = new("video/ogg", AssetMediaKind.Video)
        };

    public static string DisplayExtensions =>
        "jpg, jpeg, png, webp, gif, heic, heif, mp4, m4v, mov, webm, and ogv";

    public static bool TryGet(string fileName, out SupportedMediaType mediaType) =>
        Types.TryGetValue(Path.GetExtension(fileName), out mediaType!);

    public static bool TryGetContentType(string fileName, out string contentType)
    {
        if (TryGet(fileName, out var mediaType))
        {
            contentType = mediaType.ContentType;
            return true;
        }

        contentType = string.Empty;
        return false;
    }
}
