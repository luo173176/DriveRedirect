using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveRedirect.Core.Models;

public static class IpcOperations
{
    public const string Ping = "ping";
    public const string GetDashboard = "dashboard.get";
    public const string GetLiveSessions = "sessions.live";
    public const string GetHistory = "history.query";
    public const string ClearHistory = "history.clear";
    public const string GetRules = "rules.get";
    public const string UpsertRule = "rules.upsert";
    public const string DeleteRule = "rules.delete";
    public const string GetCompatibility = "compatibility.get";
    public const string GetObservedInstallers = "compatibility.observed";
    public const string GetSettings = "settings.get";
    public const string SaveSettings = "settings.save";
    public const string GetDiagnostics = "diagnostics.get";
    public const string ExportLogs = "diagnostics.exportLogs";
    public const string GetLogTail = "diagnostics.logTail";
    public const string BackupDatabase = "database.backup";
    public const string SetShellIntegration = "shell.set";
    public const string PlanLaunch = "launch.plan";
    public const string ReportProcessStarted = "launch.started";
}

public static class IpcNames
{
    public const string AdminPipe = "DriveRedirect.Admin.v1";
    public const string BrokerPipe = "DriveRedirect.Broker.v1";
}

public sealed class IpcRequest
{
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public string? Token { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed class IpcResponse
{
    public required string Id { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed class DeleteRuleRequest
{
    public Guid Id { get; init; }
}

public sealed class SetShellIntegrationRequest
{
    public bool Enabled { get; init; }
}

public sealed class LogTailRequest
{
    public int Lines { get; init; } = 200;
}

public sealed class LogTailResult
{
    public required string FileName { get; init; }
    public required string Content { get; init; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static T? Deserialize<T>(JsonElement? element)
        => element is null ? default : element.Value.Deserialize<T>(Options);

    public static JsonElement ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, Options);
}
