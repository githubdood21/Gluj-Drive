using System.Text.Json;
using GlujDrive.Application.Semantic;
using GlujDrive.Application.Storage;

namespace GlujDrive.Server.Security;

public sealed record ServerSettings(
    int SessionLifetimeDays,
    long MaxUploadBytes,
    long MaxBatchUploadBytes,
    double MinimumTextSimilarity,
    double MaximumTextSimilarityDrop,
    int MaximumSemanticCandidates,
    IReadOnlyList<string>? IpAllowList,
    IReadOnlyList<string>? IpDenyList);

public sealed class ServerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly AssetStorageOptions _storage;
    private readonly SemanticSearchOptions _semantic;
    private ServerSettings _settings;

    public ServerSettingsStore(
        string catalogPath,
        AssetStorageOptions storage,
        SemanticSearchOptions semantic)
    {
        _path = Path.Combine(catalogPath, "server-settings.json");
        _storage = storage;
        _semantic = semantic;
        _settings = Normalize(Load() ?? new ServerSettings(
            365,
            storage.MaxUploadBytes,
            storage.MaxBatchUploadBytes,
            semantic.MinimumTextSimilarity,
            semantic.MaximumTextSimilarityDrop,
            semantic.MaximumSemanticCandidates,
            [],
            []));
        Validate(_settings);
        Apply(_settings);
    }

    public ServerSettings Current => _settings;

    public async Task<ServerSettings> UpdateAsync(ServerSettings settings, CancellationToken cancellationToken)
    {
        settings = Normalize(settings);
        Validate(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(settings, JsonOptions),
                    cancellationToken);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
            _settings = settings;
            Apply(settings);
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ServerSettings? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new InvalidDataException("The server settings file is unreadable.", exception);
        }
    }

    private void Apply(ServerSettings settings)
    {
        _storage.MaxUploadBytes = settings.MaxUploadBytes;
        _storage.MaxBatchUploadBytes = settings.MaxBatchUploadBytes;
        _semantic.MinimumTextSimilarity = settings.MinimumTextSimilarity;
        _semantic.MaximumTextSimilarityDrop = settings.MaximumTextSimilarityDrop;
        _semantic.MaximumSemanticCandidates = settings.MaximumSemanticCandidates;
    }

    private static void Validate(ServerSettings settings)
    {
        if (settings.SessionLifetimeDays is < 1 or > 365)
            throw new ArgumentException("The session lifetime must be between 1 and 365 days.");
        const long maximumConfiguredUpload = 1024L * 1024 * 1024 * 1024;
        if (settings.MaxUploadBytes < 1024 * 1024 ||
            settings.MaxBatchUploadBytes < settings.MaxUploadBytes ||
            settings.MaxBatchUploadBytes > maximumConfiguredUpload)
            throw new ArgumentException("Upload limits are invalid; the batch limit must be at least the per-file limit.");
        if (settings.MinimumTextSimilarity is < -1 or > 1)
            throw new ArgumentException("Minimum similarity must be between -1 and 1.");
        if (settings.MaximumTextSimilarityDrop is < 0 or > 2)
            throw new ArgumentException("Maximum similarity drop must be between 0 and 2.");
        if (settings.MaximumSemanticCandidates is < 1 or > 1000)
            throw new ArgumentException("Semantic matches must be between 1 and 1000.");
        ValidateNetworkRules(settings.IpAllowList!, "allow");
        ValidateNetworkRules(settings.IpDenyList!, "deny");
    }

    private static ServerSettings Normalize(ServerSettings settings) =>
        settings with
        {
            IpAllowList = NormalizeNetworkRules(settings.IpAllowList),
            IpDenyList = NormalizeNetworkRules(settings.IpDenyList)
        };

    private static string[] NormalizeNetworkRules(IReadOnlyList<string>? rules) =>
        (rules ?? [])
            .Select(rule => rule?.Trim())
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateNetworkRules(IReadOnlyList<string> rules, string listName)
    {
        if (rules.Count > 256)
            throw new ArgumentException($"The IP {listName} list cannot contain more than 256 entries.");

        foreach (var rule in rules)
        {
            if (rule.Length > 128 || !IpNetworkRule.TryParse(rule, out _))
            {
                throw new ArgumentException(
                    $"'{rule}' is not a valid IP address or CIDR range in the IP {listName} list.");
            }
        }
    }
}
