using GlujDrive.Application.Storage;
using GlujDrive.Application.Semantic;
using GlujDrive.Infrastructure.Semantic;
using GlujDrive.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var storageOptions = builder.Configuration
    .GetSection(AssetStorageOptions.SectionName)
    .Get<AssetStorageOptions>() ?? new AssetStorageOptions();

if (string.IsNullOrWhiteSpace(storageOptions.CatalogPath))
{
    throw new InvalidOperationException("Storage:CatalogPath must not be empty.");
}

if (string.IsNullOrWhiteSpace(storageOptions.DefaultFolderPath))
{
    throw new InvalidOperationException("Storage:DefaultFolderPath must not be empty.");
}

if (storageOptions.MaxUploadBytes <= 0 ||
    storageOptions.MaxBatchUploadBytes < storageOptions.MaxUploadBytes)
{
    throw new InvalidOperationException(
        "Upload limits are invalid; the batch limit must be at least the per-file limit.");
}

var catalogPath = Path.GetFullPath(
    storageOptions.CatalogPath,
    builder.Environment.ContentRootPath);
var defaultFolderPath = Path.GetFullPath(
    storageOptions.DefaultFolderPath,
    builder.Environment.ContentRootPath);
var semanticOptions = builder.Configuration
    .GetSection(SemanticSearchOptions.SectionName)
    .Get<SemanticSearchOptions>() ?? new SemanticSearchOptions();
var semanticDataPath = Path.GetFullPath(
    semanticOptions.DataPath,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton<IAssetStorage>(new LocalAssetStorage(catalogPath, defaultFolderPath));
builder.Services.AddSingleton<IAssetVisualService>(services =>
    new CachedAssetVisualService(catalogPath, services.GetRequiredService<IAssetStorage>()));
builder.Services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
builder.Services.AddSingleton(semanticOptions);
builder.Services.AddSingleton<ISemanticSearchService>(services =>
    SemanticSearchService.Create(
        services.GetRequiredService<IAssetStorage>(),
        semanticOptions,
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        semanticDataPath,
        services.GetRequiredService<ILogger<SemanticSearchService>>()));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<FormOptions>(options =>
{
    options.MemoryBufferThreshold = 64 * 1024;
    options.MultipartBodyLengthLimit = checked(storageOptions.MaxBatchUploadBytes + (1024 * 1024));
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = checked(storageOptions.MaxBatchUploadBytes + (1024 * 1024));
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Browsers routinely cancel preview requests during reloads and off-screen eviction.
        // The client is gone, so there is no response to write and no server error to report.
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "Gluj Drive API";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Gluj Drive API v1");
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return next(context);
});

app.MapControllers();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Gluj Drive Server",
    timestampUtc = DateTimeOffset.UtcNow
}))
.WithName("GetHealth");

if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();
