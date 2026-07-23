using System.Security.Cryptography;
using System.Text.Json;

namespace GlujDrive.Server.Security;

public sealed class RootAccountStore
{
    private const int Iterations = 600_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _accountPath;
    private RootAccountRecord? _account;

    public RootAccountStore(string catalogPath)
    {
        var authPath = Path.Combine(catalogPath, "auth");
        Directory.CreateDirectory(authPath);
        _accountPath = Path.Combine(authPath, "root-account.json");
        _account = Load();
    }

    public bool IsConfigured => _account is not null;
    public string? Username => _account?.Username;

    public async Task<bool> CreateAsync(string username, string password, CancellationToken cancellationToken)
    {
        ValidateCredentials(username, password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_account is not null)
            {
                return false;
            }

            _account = CreateRecord(username.Trim(), password);
            await PersistAsync(_account, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = _account;
            if (account is null ||
                !string.Equals(account.Username, username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // Perform comparable work even for an unknown username.
                _ = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    RandomNumberGenerator.GetBytes(16),
                    Iterations,
                    HashAlgorithmName.SHA256,
                    32);
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                Convert.FromBase64String(account.Salt),
                account.Iterations,
                HashAlgorithmName.SHA256,
                32);
            return CryptographicOperations.FixedTimeEquals(
                actual,
                Convert.FromBase64String(account.PasswordHash));
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool HasCurrentSecurityStamp(string? stamp) =>
        _account is not null &&
        !string.IsNullOrWhiteSpace(stamp) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(_account.SecurityStamp),
            System.Text.Encoding.UTF8.GetBytes(stamp));

    public string GetSecurityStamp() =>
        _account?.SecurityStamp ?? throw new InvalidOperationException("The owner account is not configured.");

    public async Task UpdateAsync(string username, string password, CancellationToken cancellationToken)
    {
        ValidateCredentials(username, password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_account is null)
            {
                throw new InvalidOperationException("The owner account has not been created.");
            }

            _account = CreateRecord(username.Trim(), password) with
            {
                CreatedAtUtc = _account.CreatedAtUtc
            };
            await PersistAsync(_account, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RootAccountRecord? Load()
    {
        try
        {
            return File.Exists(_accountPath)
                ? JsonSerializer.Deserialize<RootAccountRecord>(File.ReadAllText(_accountPath), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new InvalidDataException("The owner account file is unreadable.", exception);
        }
    }

    private async Task PersistAsync(RootAccountRecord account, CancellationToken cancellationToken)
    {
        var temporaryPath = _accountPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(account, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, _accountPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static RootAccountRecord CreateRecord(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            32);
        var now = DateTimeOffset.UtcNow;
        return new RootAccountRecord(
            username,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            Iterations,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            now,
            now);
    }

    private static void ValidateCredentials(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (username.Trim().Length is < 3 or > 64)
        {
            throw new ArgumentException("The account name must contain between 3 and 64 characters.");
        }
        if (username.Trim().Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '@'))
        {
            throw new ArgumentException("The account name may use letters, numbers, hyphens, underscores, periods, and @.");
        }
        if (password.Length is < 12 or > 256)
        {
            throw new ArgumentException("The password must contain between 12 and 256 characters.");
        }
    }

    private sealed record RootAccountRecord(
        string Username,
        string Salt,
        string PasswordHash,
        int Iterations,
        string SecurityStamp,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);
}
