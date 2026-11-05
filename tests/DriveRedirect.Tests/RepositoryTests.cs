using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Configuration;
using DriveRedirect.Infrastructure.Data;

namespace DriveRedirect.Tests;

public sealed class RepositoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DriveRedirectTests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private AppRepository _repository = null!;

    public async Task InitializeAsync()
    {
        var paths = new AppPaths(_root);
        _database = new SqliteDatabase(paths);
        await _database.EnsureInitializedAsync();
        _repository = new AppRepository(_database);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Initialization_CreatesDefaultRulesAndSettings()
    {
        var rules = await _repository.GetRulesAsync();
        var settings = await _repository.GetSettingsAsync();

        Assert.Contains(rules, r => r.Name == "Program Files" && r.IsBuiltIn);
        Assert.Contains(rules, r => r.Name == "Program Files (x86)" && r.IsBuiltIn);
        Assert.Contains(rules, r => r.Name == "ProgramData" && r.IsBuiltIn);
        Assert.True(settings.AutomaticRedirectEnabled);
        Assert.Equal("D:", settings.TargetDrive);
    }

    [Fact]
    public async Task InstallationRecord_RoundTripsThroughHistory()
    {
        var record = new InstallationRecord
        {
            SoftwareName = "Acme App",
            InstallerType = InstallerType.InnoSetup,
            OriginalPath = @"C:\Program Files\Acme",
            TargetPath = @"D:\Program Files\Acme",
            ActualPath = @"D:\Program Files\Acme",
            Mode = HandlingMode.Automatic,
            Compatibility = CompatibilityLevel.High,
            Status = InstallationStatus.Redirected,
            WasRedirected = true
        };
        await _repository.SaveRecordAsync(record);

        var loaded = await _repository.GetRecordAsync(record.Id);
        var history = await _repository.QueryHistoryAsync(new HistoryQuery { Search = "Acme" });
        var summary = await _repository.GetDashboardSummaryAsync();

        Assert.NotNull(loaded);
        Assert.Equal(InstallationStatus.Redirected, loaded!.Status);
        Assert.Equal(@"D:\Program Files\Acme", loaded.ActualPath);
        Assert.Single(history.Items);
        Assert.True(summary.TotalRedirects >= 1);
    }

    [Fact]
    public async Task Settings_PersistCustomValuesAndRulesCanBeAdded()
    {
        var settings = await _repository.GetSettingsAsync();
        settings.AllowAppDataRedirection = true;
        settings.MinimumFreeSpaceGb = 7;
        await _repository.SaveSettingsAsync(settings);

        var rule = new PathRule
        {
            Name = "Acme test map",
            Kind = RuleKind.Mapping,
            SourcePattern = @"C:\Program Files\Acme",
            TargetPattern = @"D:\Program Files\Acme",
            Enabled = true
        };
        await _repository.UpsertRuleAsync(rule);

        var reloaded = await _repository.GetSettingsAsync();
        var rules = await _repository.GetRulesAsync();

        Assert.True(reloaded.AllowAppDataRedirection);
        Assert.Equal(7, reloaded.MinimumFreeSpaceGb);
        Assert.Contains(rules, r => r.Name == rule.Name);
    }
}
