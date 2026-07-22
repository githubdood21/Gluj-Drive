using Microsoft.Data.Sqlite;

namespace GlujDrive.Infrastructure.Semantic;

internal sealed class SemanticCatalog
{
    private readonly string _connectionString;

    public SemanticCatalog(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public async Task<IReadOnlyList<SemanticEmbeddingRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vector_key, asset_id, folder_id, relative_path, length,
                   modified_utc_ticks, model_fingerprint, embedding,
                   analyzed_at_utc, failure
            FROM semantic_assets;
            """;

        var records = new List<SemanticEmbeddingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var embedding = reader.IsDBNull(7)
                ? null
                : HalfVectorCodec.Decode((byte[])reader[7]);
            records.Add(new SemanticEmbeddingRecord(
                checked((ulong)reader.GetInt64(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                embedding,
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return records;
    }

    public async Task<SemanticEmbeddingRecord> UpsertSuccessAsync(
        SemanticEmbeddingRecord record,
        CancellationToken cancellationToken = default) =>
        await UpsertAsync(record with
        {
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            Failure = null
        }, cancellationToken);

    public async Task<SemanticEmbeddingRecord> UpsertFailureAsync(
        SemanticEmbeddingRecord record,
        string failure,
        CancellationToken cancellationToken = default) =>
        await UpsertAsync(record with
        {
            Embedding = null,
            AnalyzedAtUtc = DateTimeOffset.UtcNow,
            Failure = failure
        }, cancellationToken);

    public async Task PruneAsync(
        IReadOnlySet<Guid> liveAssetIds,
        CancellationToken cancellationToken = default)
    {
        var records = await ListAsync(cancellationToken);
        var stale = records.Where(record => !liveAssetIds.Contains(record.AssetId)).ToArray();
        if (stale.Length == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var record in stale)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM semantic_assets WHERE asset_id = $assetId;";
            command.Parameters.AddWithValue("$assetId", record.AssetId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM semantic_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO semantic_settings(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SemanticEmbeddingRecord> UpsertAsync(
        SemanticEmbeddingRecord record,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO semantic_assets(
                asset_id, folder_id, relative_path, length, modified_utc_ticks,
                model_fingerprint, embedding, analyzed_at_utc, failure)
            VALUES(
                $assetId, $folderId, $relativePath, $length, $modifiedTicks,
                $fingerprint, $embedding, $analyzedAt, $failure)
            ON CONFLICT(asset_id) DO UPDATE SET
                folder_id = excluded.folder_id,
                relative_path = excluded.relative_path,
                length = excluded.length,
                modified_utc_ticks = excluded.modified_utc_ticks,
                model_fingerprint = excluded.model_fingerprint,
                embedding = excluded.embedding,
                analyzed_at_utc = excluded.analyzed_at_utc,
                failure = excluded.failure
            RETURNING vector_key;
            """;
        command.Parameters.AddWithValue("$assetId", record.AssetId.ToString("D"));
        command.Parameters.AddWithValue("$folderId", record.FolderId.ToString("D"));
        command.Parameters.AddWithValue("$relativePath", record.RelativePath);
        command.Parameters.AddWithValue("$length", record.Length);
        command.Parameters.AddWithValue("$modifiedTicks", record.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$fingerprint", record.ModelFingerprint);
        command.Parameters.AddWithValue(
            "$embedding",
            record.Embedding is null ? DBNull.Value : HalfVectorCodec.Encode(record.Embedding));
        command.Parameters.AddWithValue(
            "$analyzedAt",
            record.AnalyzedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$failure", record.Failure ?? (object)DBNull.Value);
        var vectorKey = checked((ulong)(long)(await command.ExecuteScalarAsync(cancellationToken))!);
        return record with { VectorKey = vectorKey };
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS semantic_assets (
                vector_key INTEGER PRIMARY KEY AUTOINCREMENT,
                asset_id TEXT NOT NULL UNIQUE,
                folder_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                length INTEGER NOT NULL,
                modified_utc_ticks INTEGER NOT NULL,
                model_fingerprint TEXT NOT NULL,
                embedding BLOB NULL,
                analyzed_at_utc TEXT NULL,
                failure TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_semantic_assets_fingerprint
                ON semantic_assets(model_fingerprint);
            CREATE TABLE IF NOT EXISTS semantic_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
