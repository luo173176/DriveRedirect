using DriveRedirect.Core.Models;
using DriveRedirect.Core.Rules;

namespace DriveRedirect.Tests;

public sealed class PathRedirectionEngineTests
{
    private sealed class FakeDiskProbe : IDiskProbe
    {
        public DiskProbeResult Result { get; set; } = new()
        {
            Root = @"D:\",
            Exists = true,
            IsReady = true,
            IsWritable = true,
            IsFixed = true,
            TotalBytes = 500L * 1024 * 1024 * 1024,
            FreeBytes = 200L * 1024 * 1024 * 1024
        };

        public DiskProbeResult Probe(string rootOrDrive) => Result;
    }

    private readonly FakeDiskProbe _disk = new();
    private readonly PathRedirectionEngine _engine;
    private readonly AppSettings _settings = new() { TargetDrive = "D:", MinimumFreeSpaceGb = 2 };
    private readonly IReadOnlyList<PathRule> _rules = PathRedirectionEngine.CreateDefaultRules();

    public PathRedirectionEngineTests()
    {
        _engine = new PathRedirectionEngine(_disk);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Example\App", @"D:\Program Files\Example\App")]
    [InlineData(@"C:\Program Files (x86)\Example\App", @"D:\Program Files (x86)\Example\App")]
    [InlineData(@"C:\ProgramData\Example\App", @"D:\ProgramData\Example\App")]
    public void ProgramFilesAndProgramData_AreRedirectedWithHierarchy(string source, string expected)
    {
        var result = _engine.Evaluate("Example", source, _rules, _settings);

        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(expected, result.TargetPath);
    }

    [Fact]
    public void ProgramFiles_IsProtectedAndNeverRedirected()
    {
        var result = _engine.Evaluate("Bad", @"C:\Windows\System32\evil", _rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains("保护目录", result.Reason);
    }

    [Fact]
    public void NonCDrive_IsNotRedirected()
    {
        var result = _engine.Evaluate("App", @"E:\Program Files\App", _rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains("不在 C 盘", result.Reason);
    }

    [Fact]
    public void UserSpecifiedPath_IsRespected()
    {
        var result = _engine.Evaluate(
            "App", @"C:\Program Files\App", _rules, _settings, userSpecifiedPath: true);

        Assert.False(result.Allowed);
        Assert.Contains("尊重用户选择", result.Reason);
    }

    [Fact]
    public void AppData_IsDeniedWhenGlobalSwitchIsOff()
    {
        var rules = BuildAppDataRules();
        _settings.AllowAppDataRedirection = false;

        var result = _engine.Evaluate("AcmeApp", @"C:\Users\alice\AppData\Local\AcmeApp", rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains("总开关", result.Reason);
    }

    [Fact]
    public void AppData_IsDeniedWithoutApplicationWhitelist()
    {
        var rules = BuildAppDataRules();
        _settings.AllowAppDataRedirection = true;

        var result = _engine.Evaluate("OtherApp", @"C:\Users\alice\AppData\Local\AcmeApp", rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains("白名单", result.Reason);
    }

    [Fact]
    public void AppData_IsAllowedWithSwitchWhitelistAndMapping()
    {
        var rules = BuildAppDataRules();
        _settings.AllowAppDataRedirection = true;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var source = Path.Combine(localAppData, "AcmeApp");

        var result = _engine.Evaluate("AcmeApp", source, rules, _settings);

        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(@"D:\" + source[3..], result.TargetPath);
    }

    [Theory]
    [InlineData(false, true, true, true, 100, "不存在")]
    [InlineData(true, false, true, true, 100, "未就绪")]
    [InlineData(true, true, false, true, 100, "不可写")]
    [InlineData(true, true, true, false, 100, "固定磁盘")]
    [InlineData(true, true, true, true, 1, "剩余空间不足")]
    public void DiskFailures_StopAutomaticRedirect(
        bool exists, bool ready, bool writable, bool fixedDisk, long freeGb, string expectedReason)
    {
        _disk.Result = new DiskProbeResult
        {
            Root = @"D:\",
            Exists = exists,
            IsReady = ready,
            IsWritable = writable,
            IsFixed = fixedDisk,
            TotalBytes = 500L * 1024 * 1024 * 1024,
            FreeBytes = freeGb * 1024L * 1024L * 1024L
        };

        var result = _engine.Evaluate("App", @"C:\Program Files\App", _rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains(expectedReason, result.Reason);
        Assert.False(result.Allowed && result.TargetPath is not null);
    }

    [Fact]
    public void MappingTargetOutsideDDrive_IsRejected()
    {
        var rules = new List<PathRule>
        {
            new()
            {
                Name = "bad mapping",
                Kind = RuleKind.Mapping,
                SourcePattern = @"C:\Program Files",
                TargetPattern = @"E:\Program Files",
                Enabled = true
            }
        };

        var result = _engine.Evaluate("App", @"C:\Program Files\App", rules, _settings);

        Assert.False(result.Allowed);
        Assert.Contains("固定目标磁盘", result.Reason);
    }

    [Fact]
    public void ExistingNonEmptySourceDirectory_IsNotMigrated()
    {
        var root = Path.Combine(@"C:\Users\Public", "DriveRedirectTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "App");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "existing.txt"), "keep");
        try
        {
            var rules = new List<PathRule>
            {
                new()
                {
                    Name = "public test",
                    Kind = RuleKind.Mapping,
                    SourcePattern = root,
                    TargetPattern = @"D:\DriveRedirectTests",
                    Enabled = true
                }
            };

            var result = _engine.Evaluate("App", source, rules, _settings);

            Assert.False(result.Allowed);
            Assert.Contains("已存在且非空", result.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\..\Windows\System32")]
    [InlineData(@"C:\Program Files\App\..\..\Windows\System32")]
    public void DirectoryTraversal_CannotEscapeIntoProtectedSystemPaths(string input)
    {
        var result = _engine.Evaluate("App", input, _rules, _settings);
        Assert.False(result.Allowed);
    }

    private static IReadOnlyList<PathRule> BuildAppDataRules()
    {
        var rules = PathRedirectionEngine.CreateDefaultRules().ToList();
        foreach (var rule in rules.Where(r => r.Name == "用户 AppData Local"))
        {
            rule.Enabled = true;
            rule.AllowAppData = true;
        }
        rules.Add(new PathRule
        {
            Name = "Acme AppData",
            Kind = RuleKind.AppWhitelist,
            SourcePattern = "AcmeApp",
            Enabled = true,
            AllowAppData = true
        });
        return rules;
    }
}
