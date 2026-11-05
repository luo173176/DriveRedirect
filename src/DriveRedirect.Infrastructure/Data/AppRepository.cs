using System.Text.Json;
using DriveRedirect.Core.Models;
using Microsoft.Data.Sqlite;

namespace DriveRedirect.Infrastructure.Data;

public sealed class AppRepository
{
    private readonly SqliteDatabase _database;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AppRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public SqliteDatabase Database => _database;

    public async Task<IReadOnlyList<PathRule>> GetRulesAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, source_pattern, target_pattern, enabled, priority, allow_appdata, note, is_builtin, created_at
            FROM path_rules
            ORDER BY priority DESC, name;
            """;
        var rules = new List<PathRule>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new PathRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Kind = (RuleKind)reader.GetInt32(2),
                SourcePattern = reader.GetString(3),
                TargetPattern = reader.IsDBNull(4) ? null : reader.GetString(4),
                Enabled = reader.GetInt32(5) != 0,
                Priority = reader.GetInt32(6),
                AllowAppData = reader.GetInt32(7) != 0,
                Note = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsBuiltIn = reader.GetInt32(9) != 0,
                CreatedAt = DateTimeOffset.Parse(reader.GetString(10))
            });
        }
        return rules;
    }

    public async Task<PathRule> UpsertRuleAsync(PathRule rule, CancellationToken ct = default)
    {
        ValidateRule(rule);
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO path_rules
                (id, name, kind, source_pattern, target_pattern, enabled, priority, allow_appdata, note, is_builtin, created_at)
                VALUES ($id, $name, $kind, $source, $target, $enabled, $priority, $allowAppData, $note, 0, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name,
                    kind=excluded.kind,
                    source_pattern=excluded.source_pattern,
                    target_pattern=excluded.target_pattern,
                    enabled=excluded.enabled,
                    priority=excluded.priority,
                    allow_appdata=excluded.allow_appdata,
                    note=excluded.note;
                """;
            AddRuleParameters(command, rule);
            await command.ExecuteNonQueryAsync(ct);
            return rule;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM path_rules WHERE id=$id AND is_builtin=0;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetRuleEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE path_rules SET enabled=$enabled WHERE id=$id;";
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='app';";
        var json = await command.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        settings.TargetDrive = string.IsNullOrWhiteSpace(settings.TargetDrive) ? "D:" : settings.TargetDrive.ToUpperInvariant().TrimEnd('\\');
        settings.MinimumFreeSpaceGb = Math.Clamp(settings.MinimumFreeSpaceGb, 1, 1000);
        settings.HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 0, 3650);
        var json = JsonSerializer.Serialize(settings);

        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value) VALUES ('app', $value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("$value", json);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveRecordAsync(InstallationRecord record, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO installation_records
                (id, started_at, ended_at, installer_type, software_name, installer_path, installer_version, installer_hash,
                 process_id, parent_process_id, original_path, target_path, actual_path, mode, compatibility, status,
                 adapter_id, adapter_version, strategy, launch_arguments, msi_log_path, exit_code, failure_reason,
                 verification_source, was_redirected, user_sid, metadata_json, last_updated_at)
                VALUES
                ($id, $startedAt, $endedAt, $installerType, $softwareName, $installerPath, $installerVersion, $installerHash,
                 $processId, $parentProcessId, $originalPath, $targetPath, $actualPath, $mode, $compatibility, $status,
                 $adapterId, $adapterVersion, $strategy, $launchArguments, $msiLogPath, $exitCode, $failureReason,
                 $verificationSource, $wasRedirected, $userSid, $metadataJson, $lastUpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    ended_at=excluded.ended_at,
                    installer_type=excluded.installer_type,
                    software_name=excluded.software_name,
                    installer_path=excluded.installer_path,
                    installer_version=excluded.installer_version,
                    installer_hash=excluded.installer_hash,
                    process_id=excluded.process_id,
                    parent_process_id=excluded.parent_process_id,
                    original_path=excluded.original_path,
                    target_path=excluded.target_path,
                    actual_path=excluded.actual_path,
                    mode=excluded.mode,
                    compatibility=excluded.compatibility,
                    status=excluded.status,
                    adapter_id=excluded.adapter_id,
                    adapter_version=excluded.adapter_version,
                    strategy=excluded.strategy,
                    launch_arguments=excluded.launch_arguments,
                    msi_log_path=excluded.msi_log_path,
                    exit_code=excluded.exit_code,
                    failure_reason=excluded.failure_reason,
                    verification_source=excluded.verification_source,
                    was_redirected=excluded.was_redirected,
                    user_sid=excluded.user_sid,
                    metadata_json=excluded.metadata_json,
                    last_updated_at=excluded.last_updated_at;
                """;
            AddRecordParameters(command, record);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<InstallationRecord?> GetRecordAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM installation_records WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<InstallationRecord?> FindPendingLaunchAsync(
        string installerPath,
        DateTimeOffset notBefore,
        CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM installation_records
            WHERE status IN (0,1,2)
              AND installer_path = $path
              AND started_at >= $notBefore
            ORDER BY started_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", installerPath);
        command.Parameters.AddWithValue("$notBefore", notBefore.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<InstallationRecord?> FindActiveRecordByProcessIdAsync(
        int processId,
        CancellationToken ct = default)
    {
        if (processId <= 0)
            return null;
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM installation_records
            WHERE status IN (0,1,2)
              AND process_id = $processId
            ORDER BY started_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$processId", processId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<bool> MarkProcessStartedAsync(Guid sessionId, int processId, CancellationToken ct = default)
    {
        var record = await GetRecordAsync(sessionId, ct);
        if (record is null || record.Status is not (InstallationStatus.Planned or InstallationStatus.Launching or InstallationStatus.Running))
            return false;
        record.ProcessId = processId;
        record.Status = InstallationStatus.Running;
        record.LastUpdatedAt = DateTimeOffset.Now;
        await SaveRecordAsync(record, ct);
        return true;
    }

    public async Task<IReadOnlyList<InstallationRecord>> GetActiveRecordsAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM installation_records WHERE status IN (0,1,2) ORDER BY started_at;";
        var items = new List<InstallationRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadRecord(reader));
        return items;
    }

    public async Task<int> CountPendingSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM installation_records WHERE status IN (0,1,2);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        var where = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("(software_name LIKE $search OR original_path LIKE $search OR target_path LIKE $search OR installer_path LIKE $search)");
            parameters.Add(new SqliteParameter("$search", "%" + EscapeLike(query.Search.Trim()) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(query.ResultFilter) && !query.ResultFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (query.ResultFilter.Equals("redirected", StringComparison.OrdinalIgnoreCase))
                where.Add("was_redirected=1 AND status IN (3,4,10)");
            else if (query.ResultFilter.Equals("passed", StringComparison.OrdinalIgnoreCase))
                where.Add("(was_redirected=0 OR status=8)");
            else if (query.ResultFilter.Equals("failed", StringComparison.OrdinalIgnoreCase))
                where.Add("status IN (5,6,7)");
            else if (query.ResultFilter.Equals("running", StringComparison.OrdinalIgnoreCase))
                where.Add("status IN (0,1,2)");
        }
        if (!string.IsNullOrWhiteSpace(query.InstallerFilter) && !query.InstallerFilter.Equals("all", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<InstallerType>(query.InstallerFilter, true, out var installerType))
        {
            where.Add("installer_type=$installerType");
            parameters.Add(new SqliteParameter("$installerType", (int)installerType));
        }

        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var orderColumn = query.SortBy.ToLowerInvariant() switch
        {
            "name" => "software_name",
            "installer" => "installer_type",
            "status" => "status",
            "target" => "COALESCE(actual_path, target_path, '')",
            _ => "started_at"
        };
        var orderDir = query.Descending ? "DESC" : "ASC";

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM installation_records" + whereSql + ";";
        foreach (var p in parameters)
            countCommand.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct) ?? 0);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM installation_records{whereSql} ORDER BY {orderColumn} {orderDir}, started_at DESC LIMIT $take OFFSET $skip;";
        foreach (var p in parameters)
            command.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        command.Parameters.AddWithValue("$take", Math.Clamp(query.Take, 1, 500));
        command.Parameters.AddWithValue("$skip", Math.Max(0, query.Skip));

        var items = new List<InstallationRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadRecord(reader));
        return new HistoryResult { Items = items, Total = total };
    }

    public async Task<IReadOnlyList<InstallationRecord>> GetRecentRecordsAsync(int take, CancellationToken ct = default)
    {
        var result = await QueryHistoryAsync(new HistoryQuery { Take = take }, ct);
        return result.Items;
    }

    public async Task<IReadOnlyList<LiveInstallSession>> GetLiveSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM installation_records
            WHERE status IN (0,1,2)
            ORDER BY started_at DESC LIMIT 50;
            """;
        var records = new List<InstallationRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            records.Add(ReadRecord(reader));
        return records.Select(r => new LiveInstallSession
        {
            Id = r.Id,
            SoftwareName = r.SoftwareName,
            InstallerType = r.InstallerType,
            InstallerPath = r.InstallerPath,
            ProcessId = r.ProcessId,
            OriginalPath = r.OriginalPath,
            TargetPath = r.TargetPath,
            Status = r.Status,
            Mode = r.Mode,
            Compatibility = r.Compatibility,
            StartedAt = r.StartedAt,
            AdapterId = r.AdapterId,
            StatusDetail = r.Status == InstallationStatus.Planned ? "已生成重定向计划，等待安装器启动" :
                r.Status == InstallationStatus.Launching ? "正在启动安装器" : "安装器正在运行",
            WasRedirected = r.WasRedirected
        }).ToList();
    }

    public async Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        var today = DateTimeOffset.Now.Date;
        var todayIso = today.ToString("O");
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN started_at >= $today THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN started_at >= $today AND was_redirected=1 AND status IN (3,4,10) THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN was_redirected=1 AND status IN (3,4,10) THEN 1 ELSE 0 END), 0)
            FROM installation_records;
            """;
        command.Parameters.AddWithValue("$today", todayIso);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new DashboardSummary
            {
                TodayInstalls = reader.GetInt32(0),
                TodayRedirects = reader.GetInt32(1),
                TotalRedirects = reader.GetInt32(2)
            };
        }
        return new DashboardSummary();
    }

    public async Task SaveObservationAsync(ObservedInstaller observation, InstallerArtifact artifact, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO installer_observations
                (id, detected_at, installer_path, installer_hash, installer_type, software_name, compatibility, mode, adapter_id, reason, metadata_json)
                VALUES ($id, $detectedAt, $path, $hash, $type, $name, $compatibility, $mode, $adapterId, $reason, $metadata);
                """;
            command.Parameters.AddWithValue("$id", observation.Id.ToString("D"));
            command.Parameters.AddWithValue("$detectedAt", observation.DetectedAt.ToString("O"));
            command.Parameters.AddWithValue("$path", observation.InstallerPath);
            command.Parameters.AddWithValue("$hash", (object?)observation.Hash ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", (int)observation.InstallerType);
            command.Parameters.AddWithValue("$name", (object?)observation.SoftwareName ?? DBNull.Value);
            command.Parameters.AddWithValue("$compatibility", (int)observation.Compatibility);
            command.Parameters.AddWithValue("$mode", (int)observation.Mode);
            command.Parameters.AddWithValue("$adapterId", (object?)observation.AdapterId ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)observation.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
            {
                artifact.ProductName,
                artifact.CompanyName,
                artifact.ProductVersion,
                artifact.SizeBytes,
                artifact.DetectionSignals
            }));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ObservedInstaller>> GetObservationsAsync(int take = 200, CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, detected_at, installer_path, installer_type, software_name, compatibility, mode, reason, installer_hash, adapter_id
            FROM installer_observations ORDER BY detected_at DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 1000));
        var items = new List<ObservedInstaller>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ObservedInstaller
            {
                Id = Guid.Parse(reader.GetString(0)),
                DetectedAt = DateTimeOffset.Parse(reader.GetString(1)),
                InstallerPath = reader.GetString(2),
                InstallerType = (InstallerType)reader.GetInt32(3),
                SoftwareName = reader.IsDBNull(4) ? null : reader.GetString(4),
                Compatibility = (CompatibilityLevel)reader.GetInt32(5),
                Mode = (HandlingMode)reader.GetInt32(6),
                Reason = reader.IsDBNull(7) ? null : reader.GetString(7),
                Hash = reader.IsDBNull(8) ? null : reader.GetString(8),
                AdapterId = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return items;
    }

    public async Task<IReadOnlyDictionary<InstallerType, (int Count, DateTimeOffset? LastObserved)>> GetObservationSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT installer_type, COUNT(*), MAX(detected_at)
            FROM installer_observations GROUP BY installer_type;
            """;
        var result = new Dictionary<InstallerType, (int, DateTimeOffset?)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[(InstallerType)reader.GetInt32(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)));
        return result;
    }

    public async Task AddExceptionAsync(string source, Exception exception, string? context = null, CancellationToken ct = default)
    {
        try
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                await using var connection = await _database.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO exceptions(occurred_at, source, message, stack_trace, context)
                    VALUES ($time, $source, $message, $stack, $context);
                    """;
                command.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$message", exception.Message);
                command.Parameters.AddWithValue("$stack", (object?)exception.StackTrace ?? DBNull.Value);
                command.Parameters.AddWithValue("$context", (object?)context ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            // Diagnostics writes must never mask the original failure.
        }
    }

    public async Task<int> ClearHistoryAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM installation_records WHERE status NOT IN (0,1,2);";
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MarkInterruptedAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE installation_records
                SET status=$interrupted, ended_at=COALESCE(ended_at, $now),
                    failure_reason=COALESCE(failure_reason, '服务重启导致监控中断；未自动迁移任何已安装软件。'),
                    last_updated_at=$now
                WHERE status IN (0,1,2);
                """;
            command.Parameters.AddWithValue("$interrupted", (int)InstallationStatus.Interrupted);
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(int Exceptions, string? LastError)> GetExceptionSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), (SELECT message FROM exceptions ORDER BY occurred_at DESC LIMIT 1)
            FROM exceptions WHERE occurred_at >= $since;
            """;
        command.Parameters.AddWithValue("$since", DateTimeOffset.Now.AddHours(-24).ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        return (0, null);
    }

    public async Task<string> BackupDatabaseAsync(string? destinationDirectory = null, CancellationToken ct = default)
    {
        await _database.EnsureInitializedAsync(ct);
        destinationDirectory ??= Path.Combine(_database.Paths.ExportDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, $"DriveRedirect-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await using var source = await _database.OpenConnectionAsync(ct);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
        return destination;
    }

    private static void ValidateRule(PathRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("规则名称不能为空。");
        if (string.IsNullOrWhiteSpace(rule.SourcePattern))
            throw new ArgumentException("源路径或匹配模式不能为空。");
        if (rule.Kind == RuleKind.Mapping)
        {
            _ = Core.Rules.WindowsPathTools.NormalizeDirectory(rule.SourcePattern);
            if (string.IsNullOrWhiteSpace(rule.TargetPattern))
                throw new ArgumentException("路径映射必须有目标路径。");
            var target = Core.Rules.WindowsPathTools.NormalizeDirectory(rule.TargetPattern);
            var root = Path.GetPathRoot(target);
            if (!string.Equals(root, @"D:\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("目标路径必须位于固定 D 盘。");
        }
        if (rule.Kind == RuleKind.ProtectedPath)
            _ = Core.Rules.WindowsPathTools.NormalizeDirectory(rule.SourcePattern);
    }

    private static void AddRuleParameters(SqliteCommand command, PathRule rule)
    {
        command.Parameters.AddWithValue("$id", rule.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", rule.Name.Trim());
        command.Parameters.AddWithValue("$kind", (int)rule.Kind);
        command.Parameters.AddWithValue("$source", rule.SourcePattern.Trim());
        command.Parameters.AddWithValue("$target", (object?)rule.TargetPattern?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$priority", rule.Priority);
        command.Parameters.AddWithValue("$allowAppData", rule.AllowAppData ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)rule.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", rule.CreatedAt.ToString("O"));
    }

    private static void AddRecordParameters(SqliteCommand command, InstallationRecord r)
    {
        command.Parameters.AddWithValue("$id", r.Id.ToString("D"));
        command.Parameters.AddWithValue("$startedAt", r.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", r.EndedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$installerType", (int)r.InstallerType);
        command.Parameters.AddWithValue("$softwareName", r.SoftwareName);
        command.Parameters.AddWithValue("$installerPath", (object?)r.InstallerPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$installerVersion", (object?)r.InstallerVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$installerHash", (object?)r.InstallerHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$processId", (object?)r.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentProcessId", (object?)r.ParentProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$originalPath", (object?)r.OriginalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetPath", (object?)r.TargetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualPath", (object?)r.ActualPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)r.Mode);
        command.Parameters.AddWithValue("$compatibility", (int)r.Compatibility);
        command.Parameters.AddWithValue("$status", (int)r.Status);
        command.Parameters.AddWithValue("$adapterId", (object?)r.AdapterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$adapterVersion", (object?)r.AdapterVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$strategy", (object?)r.Strategy ?? DBNull.Value);
        command.Parameters.AddWithValue("$launchArguments", (object?)r.LaunchArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$msiLogPath", (object?)r.MsiLogPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$exitCode", (object?)r.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureReason", (object?)r.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$verificationSource", (object?)r.VerificationSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$wasRedirected", r.WasRedirected ? 1 : 0);
        command.Parameters.AddWithValue("$userSid", (object?)r.UserSid ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", (object?)r.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastUpdatedAt", r.LastUpdatedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static InstallationRecord ReadRecord(SqliteDataReader reader)
    {
        int Ordinal(string name) => reader.GetOrdinal(name);
        T? Nullable<T>(string name) where T : class =>
            reader.IsDBNull(Ordinal(name)) ? null : (T)reader.GetValue(Ordinal(name));
        int? NullableInt(string name) => reader.IsDBNull(Ordinal(name)) ? null : reader.GetInt32(Ordinal(name));
        DateTimeOffset? NullableDate(string name) => reader.IsDBNull(Ordinal(name)) ? null : DateTimeOffset.Parse(reader.GetString(Ordinal(name)));

        return new InstallationRecord
        {
            Id = Guid.Parse(reader.GetString(Ordinal("id"))),
            StartedAt = DateTimeOffset.Parse(reader.GetString(Ordinal("started_at"))),
            EndedAt = NullableDate("ended_at"),
            InstallerType = (InstallerType)reader.GetInt32(Ordinal("installer_type")),
            SoftwareName = reader.GetString(Ordinal("software_name")),
            InstallerPath = Nullable<string>("installer_path"),
            InstallerVersion = Nullable<string>("installer_version"),
            InstallerHash = Nullable<string>("installer_hash"),
            ProcessId = NullableInt("process_id"),
            ParentProcessId = NullableInt("parent_process_id"),
            OriginalPath = Nullable<string>("original_path"),
            TargetPath = Nullable<string>("target_path"),
            ActualPath = Nullable<string>("actual_path"),
            Mode = (HandlingMode)reader.GetInt32(Ordinal("mode")),
            Compatibility = (CompatibilityLevel)reader.GetInt32(Ordinal("compatibility")),
            Status = (InstallationStatus)reader.GetInt32(Ordinal("status")),
            AdapterId = Nullable<string>("adapter_id"),
            AdapterVersion = Nullable<string>("adapter_version"),
            Strategy = Nullable<string>("strategy"),
            LaunchArguments = Nullable<string>("launch_arguments"),
            MsiLogPath = Nullable<string>("msi_log_path"),
            ExitCode = NullableInt("exit_code"),
            FailureReason = Nullable<string>("failure_reason"),
            VerificationSource = Nullable<string>("verification_source"),
            WasRedirected = reader.GetInt32(Ordinal("was_redirected")) != 0,
            UserSid = Nullable<string>("user_sid"),
            MetadataJson = Nullable<string>("metadata_json"),
            LastUpdatedAt = NullableDate("last_updated_at")
        };
    }

    private static string EscapeLike(string input)
        => input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
