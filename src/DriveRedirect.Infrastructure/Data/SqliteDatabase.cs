using DriveRedirect.Core.Rules;
using DriveRedirect.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace DriveRedirect.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly AppPaths _paths;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public AppPaths Paths => _paths;
    public string ConnectionString => _connectionString;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=8000;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;
            _paths.EnsureDirectories();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=8000;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            var version = await GetUserVersionAsync(connection, ct);
            if (version < 1)
                await MigrateToV1Async(connection, ct);

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private static async Task<long> GetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value ?? 0);
    }

    private static async Task MigrateToV1Async(SqliteConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS path_rules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                source_pattern TEXT NOT NULL,
                target_pattern TEXT NULL,
                enabled INTEGER NOT NULL,
                priority INTEGER NOT NULL,
                allow_appdata INTEGER NOT NULL,
                note TEXT NULL,
                is_builtin INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS installation_records (
                id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                installer_type INTEGER NOT NULL,
                software_name TEXT NOT NULL,
                installer_path TEXT NULL,
                installer_version TEXT NULL,
                installer_hash TEXT NULL,
                process_id INTEGER NULL,
                parent_process_id INTEGER NULL,
                original_path TEXT NULL,
                target_path TEXT NULL,
                actual_path TEXT NULL,
                mode INTEGER NOT NULL,
                compatibility INTEGER NOT NULL,
                status INTEGER NOT NULL,
                adapter_id TEXT NULL,
                adapter_version TEXT NULL,
                strategy TEXT NULL,
                launch_arguments TEXT NULL,
                msi_log_path TEXT NULL,
                exit_code INTEGER NULL,
                failure_reason TEXT NULL,
                verification_source TEXT NULL,
                was_redirected INTEGER NOT NULL,
                user_sid TEXT NULL,
                metadata_json TEXT NULL,
                last_updated_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_records_started ON installation_records(started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_records_status ON installation_records(status);
            CREATE INDEX IF NOT EXISTS ix_records_name ON installation_records(software_name);
            CREATE INDEX IF NOT EXISTS ix_records_type ON installation_records(installer_type);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS installer_observations (
                id TEXT PRIMARY KEY,
                detected_at TEXT NOT NULL,
                installer_path TEXT NOT NULL,
                installer_hash TEXT NULL,
                installer_type INTEGER NOT NULL,
                software_name TEXT NULL,
                compatibility INTEGER NOT NULL,
                mode INTEGER NOT NULL,
                adapter_id TEXT NULL,
                reason TEXT NULL,
                metadata_json TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_observations_detected ON installer_observations(detected_at DESC);
            CREATE INDEX IF NOT EXISTS ix_observations_type ON installer_observations(installer_type);

            CREATE TABLE IF NOT EXISTS exceptions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                source TEXT NOT NULL,
                message TEXT NOT NULL,
                stack_trace TEXT NULL,
                context TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_exceptions_time ON exceptions(occurred_at DESC);
            """;
        await command.ExecuteNonQueryAsync(ct);

        await InsertDefaultRulesAsync(connection, (SqliteTransaction)transaction, ct);
        await InsertDefaultSettingsAsync(connection, (SqliteTransaction)transaction, ct);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = (SqliteTransaction)transaction;
        versionCommand.CommandText = "PRAGMA user_version=1;";
        await versionCommand.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task InsertDefaultRulesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        foreach (var rule in PathRedirectionEngine.CreateDefaultRules())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO path_rules
                (id, name, kind, source_pattern, target_pattern, enabled, priority, allow_appdata, note, is_builtin, created_at)
                VALUES ($id, $name, $kind, $source, $target, $enabled, $priority, $allowAppData, $note, $builtIn, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", rule.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", rule.Name);
            command.Parameters.AddWithValue("$kind", (int)rule.Kind);
            command.Parameters.AddWithValue("$source", rule.SourcePattern);
            command.Parameters.AddWithValue("$target", (object?)rule.TargetPattern ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$priority", rule.Priority);
            command.Parameters.AddWithValue("$allowAppData", rule.AllowAppData ? 1 : 0);
            command.Parameters.AddWithValue("$note", (object?)rule.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$builtIn", rule.IsBuiltIn ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", rule.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertDefaultSettingsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var settingsJson = System.Text.Json.JsonSerializer.Serialize(new Core.Models.AppSettings());
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO settings(key, value) VALUES ('app', $value);";
        command.Parameters.AddWithValue("$value", settingsJson);
        await command.ExecuteNonQueryAsync(ct);
    }
}
