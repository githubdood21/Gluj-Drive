using GlujDrive.Application.Storage;
using GlujDrive.Application.Semantic;
using GlujDrive.Infrastructure.Semantic;
using GlujDrive.Infrastructure.Storage;
using GlujDrive.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Threading.RateLimiting;

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

var catalogPath = ResolveConfiguredPath(
    storageOptions.CatalogPath,
    builder.Environment.ContentRootPath);
var defaultFolderPath = ResolveConfiguredPath(
    storageOptions.DefaultFolderPath,
    builder.Environment.ContentRootPath);
var semanticOptions = builder.Configuration
    .GetSection(SemanticSearchOptions.SectionName)
    .Get<SemanticSearchOptions>() ?? new SemanticSearchOptions();
var semanticDataPath = ResolveConfiguredPath(
    semanticOptions.DataPath,
    builder.Environment.ContentRootPath);
var serverSettings = new ServerSettingsStore(catalogPath, storageOptions, semanticOptions);
var rootAccounts = new RootAccountStore(catalogPath);
var dataProtectionPath = Path.Combine(catalogPath, "auth", "keys");
Directory.CreateDirectory(dataProtectionPath);

if (storageOptions.MaxUploadBytes <= 0 ||
    storageOptions.MaxBatchUploadBytes < storageOptions.MaxUploadBytes)
{
    throw new InvalidOperationException(
        "Upload limits are invalid; the batch limit must be at least the per-file limit.");
}
var configuredFfmpegPath = builder.Configuration["Media:FfmpegPath"] ??
    "runtime/ffmpeg/win-x64/ffmpeg.exe";
var ffmpegPath = Path.IsPathRooted(configuredFfmpegPath)
    ? configuredFfmpegPath
    : Path.GetFullPath(configuredFfmpegPath, builder.Environment.ContentRootPath);

if (!File.Exists(ffmpegPath))
{
    // Development machines may already provide FFmpeg through PATH.
    ffmpegPath = "ffmpeg";
}
semanticOptions.BundledPackagePath = ResolveConfiguredPath(
    semanticOptions.BundledPackagePath,
    builder.Environment.ContentRootPath);
semanticOptions.BundledPackageSha256Path = ResolveConfiguredPath(
    semanticOptions.BundledPackageSha256Path,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(serverSettings);
builder.Services.AddSingleton(rootAccounts);
builder.Services.AddSingleton<IAssetStorage>(new LocalAssetStorage(catalogPath, defaultFolderPath));
builder.Services.AddSingleton<IAssetVisualService>(services =>
    new CachedAssetVisualService(
        catalogPath,
        services.GetRequiredService<IAssetStorage>(),
        ffmpegPath));
builder.Services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
builder.Services.AddSingleton(semanticOptions);
builder.Services.AddSingleton<ISemanticSearchService>(services =>
    SemanticSearchService.Create(
        services.GetRequiredService<IAssetStorage>(),
        services.GetRequiredService<IAssetVisualService>(),
        semanticOptions,
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        semanticDataPath,
        services.GetRequiredService<ILogger<SemanticSearchService>>()));
builder.Services.AddControllers();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .ProtectKeysWithDpapi()
    .SetApplicationName("GlujDrive");
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "GlujDrive.Root";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var accountStore = context.HttpContext.RequestServices.GetRequiredService<RootAccountStore>();
            if (!accountStore.HasCurrentSecurityStamp(context.Principal?.FindFirst("security_stamp")?.Value))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
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
var applicationVersion = System.Reflection.Assembly
    .GetExecutingAssembly()
    .GetName()
    .Version?
    .ToString(3) ?? "unknown";

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

app.UseForwardedHeaders();
app.UseMiddleware<NetworkAccessMiddleware>();

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
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return next(context);
});

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<RequestSecurityMiddleware>();

app.MapControllers();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Gluj Drive Server",
    version = applicationVersion,
    timestampUtc = DateTimeOffset.UtcNow
}))
.WithName("GetHealth");

if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();

static string ResolveConfiguredPath(string configuredPath, string contentRootPath)
{
    var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
    return Path.IsPathRooted(expandedPath)
        ? Path.GetFullPath(expandedPath)
        : Path.GetFullPath(expandedPath, contentRootPath);
}
