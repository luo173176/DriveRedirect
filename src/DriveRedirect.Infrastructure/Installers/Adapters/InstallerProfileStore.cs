using System.Text.Json;
using System.Text.Json.Serialization;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;

namespace DriveRedirect.Infrastructure.Installers.Adapters;

public sealed class InstallerProfile
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Verified { get; set; }
    public string? FileNamePattern { get; set; }
    public string? Sha256 { get; set; }
    public string? OriginalPathTemplate { get; set; }
    public string? ArgumentTemplate { get; set; }
    public CompatibilityLevel Compatibility { get; set; } = CompatibilityLevel.Experimental;
    public string? Notes { get; set; }
}

public sealed class InstallerProfileStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<InstallerProfile>? _cache;
    private DateTime _cacheWriteTime;

    public InstallerProfileStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<InstallerProfile>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_paths.ProfilesPath))
            {
                await WriteDefaultProfilesAsync(ct);
                return _cache ?? [];
            }

            var writeTime = File.GetLastWriteTimeUtc(_paths.ProfilesPath);
            if (_cache is not null && writeTime == _cacheWriteTime)
                return _cache;

            var json = await File.ReadAllTextAsync(_paths.ProfilesPath, ct);
            _cache = JsonSerializer.Deserialize<List<InstallerProfile>>(json, JsonOptions) ?? [];
            _cacheWriteTime = writeTime;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InstallerProfile?> FindMatchAsync(InstallerArtifact artifact, CancellationToken ct = default)
    {
        var profiles = await LoadAsync(ct);
        foreach (var profile in profiles.Where(p => p.Enabled))
        {
            var hasIdentifier = !string.IsNullOrWhiteSpace(profile.FileNamePattern) ||
                                !string.IsNullOrWhiteSpace(profile.Sha256);
            if (!hasIdentifier)
                continue;
            if (!string.IsNullOrWhiteSpace(profile.FileNamePattern) &&
                !Wildcard(artifact.FileName, profile.FileNamePattern))
                continue;
            if (!string.IsNullOrWhiteSpace(profile.Sha256) &&
                !string.Equals(profile.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;
            return profile;
        }
        return null;
    }

    private async Task WriteDefaultProfilesAsync(CancellationToken ct)
    {
        var examples = new List<InstallerProfile>
        {
            new()
            {
                Name = "示例：已验证的自定义安装器（默认禁用）",
                Enabled = false,
                Verified = false,
                FileNamePattern = "AcmeSetup*.exe",
                OriginalPathTemplate = @"C:\Program Files\Acme",
                ArgumentTemplate = "/VERYSILENT /DIR=\"{target}\"",
                Compatibility = CompatibilityLevel.Experimental,
                Notes = "仅在测试安装包、确认参数与卸载更新行为后，将 Enabled 与 Verified 同时设为 true。"
            }
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.ProfilesPath)!);
            await File.WriteAllTextAsync(_paths.ProfilesPath, JsonSerializer.Serialize(examples, JsonOptions), ct);
            _cache = examples;
            _cacheWriteTime = File.GetLastWriteTimeUtc(_paths.ProfilesPath);
        }
        catch
        {
            _cache = [];
        }
    }

    private static bool Wildcard(string value, string pattern)
    {
        int v = 0, p = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                v++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
