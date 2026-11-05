using DriveRedirect.Infrastructure.Installers.Detection;

namespace DriveRedirect.Tests;

public sealed class MsiFixtureTests
{
    [Fact]
    public void ExplicitInstallDir_IsReadAsPublicProperty()
    {
        var path = FixturePath("AcmeExplicit.msi");
        var info = new MsiNativeReader().Read(path);

        Assert.Equal("Acme Explicit", info.ProductName);
        Assert.Equal("INSTALLDIR", info.DirectoryProperty);
        Assert.True(info.HasExplicitInstallProperty);
        Assert.Equal(@"C:\Program Files\AcmeExplicit", info.DefaultInstallPath);
        Assert.Equal("4F0AA293-40B4-4C63-9C57-2A7D27F6E2B1", NormalizeCode(info.UpgradeCode));
    }

    [Fact]
    public void PrivateDirectory_RequiresMsiTransformFallback()
    {
        var path = FixturePath("AcmePrivate.msi");
        var info = new MsiNativeReader().Read(path);

        Assert.Equal("Acme Private", info.ProductName);
        Assert.Equal("AcmePrivateDir", info.DirectoryProperty);
        Assert.False(info.HasExplicitInstallProperty);
        Assert.Equal(@"C:\Program Files\AcmePrivate", info.DefaultInstallPath);
    }

    [Fact]
    public async Task TransformBuilder_ProducesNonEmptyMst()
    {
        var msi = FixturePath("AcmePrivate.msi");
        var info = new MsiNativeReader().Read(msi);
        var output = Path.Combine(Path.GetTempPath(), "DriveRedirectTests", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new MsiTransformBuilder();
            var transform = await builder.BuildAsync(
                msi,
                @"D:\Program Files\AcmePrivate",
                info.DirectoryProperty!,
                output,
                Guid.NewGuid());

            Assert.True(File.Exists(transform));
            Assert.True(new FileInfo(transform).Length > 0);
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static string FixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.True(File.Exists(path), "缺少测试 MSI：" + path + "。请先运行 build.ps1 -BuildFixtures。");
        return path;
    }

    private static string NormalizeCode(string? code)
        => (code ?? "").Trim().Trim('{', '}').ToUpperInvariant();
}
