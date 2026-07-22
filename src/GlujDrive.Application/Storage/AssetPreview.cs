namespace GlujDrive.Application.Storage;

public sealed record AssetPreview(Stream Content, string ContentType) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
