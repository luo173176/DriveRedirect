using DriveRedirect.Core.Installer;
using DriveRedirect.Core.Models;
using DriveRedirect.Infrastructure.Installers.Adapters;

namespace DriveRedirect.Tests;

public sealed class InstallerArgumentTests
{
    [Fact]
    public void InnoArguments_AppendDirAndPreserveExistingArguments()
    {
        var args = AdapterHelpers.BuildInnoArguments("/SILENT", @"D:\Program Files\Acme");

        Assert.Equal("/SILENT /DIR=\"D:\\Program Files\\Acme\"", args);
    }

    [Fact]
    public void NsisArguments_PutDLastAndDoNotQuotePath()
    {
        var args = AdapterHelpers.BuildNsisArguments("/S", @"D:\Program Files\Acme");

        Assert.EndsWith("/D=D:\\Program Files\\Acme", args);
        Assert.DoesNotContain("\"/D=", args);
        Assert.Equal("/S /D=D:\\Program Files\\Acme", args);
    }

    [Theory]
    [InlineData("Acme App Setup", "Acme App")]
    [InlineData("Acme App Installer", "Acme App")]
    [InlineData("Acme App Installation", "Acme App")]
    [InlineData("AcmeSetup", "Acme")]
    [InlineData("Setup", "Acme")]
    public void SoftwareName_StripsSetupSuffixes(string input, string expectedPrefix)
    {
        var result = InstallerNames.InferSoftwareName(input, null, "AcmeSetup.exe");

        Assert.Equal(expectedPrefix, result);
    }

    [Theory]
    [InlineData("/x {GUID}", true)]
    [InlineData("/uninstall", true)]
    [InlineData("/repair", true)]
    [InlineData("/update", true)]
    [InlineData("/SILENT /DIR=C:\\Program Files\\App", false)]
    public void MaintenanceInvocation_IsDetected(string arguments, bool expected)
    {
        Assert.Equal(expected, InstallerNames.IsMaintenanceInvocation(arguments));
    }

    [Theory]
    [InlineData(InstallerType.Nsis, "/S /D=D:\\App", true)]
    [InlineData(InstallerType.Nsis, "/S", false)]
    [InlineData(InstallerType.InnoSetup, "/DIR=D:\\App", true)]
    [InlineData(InstallerType.InnoSetup, "/SILENT", false)]
    [InlineData(InstallerType.WindowsMsi, "INSTALLDIR=D:\\App", true)]
    public void PathOverride_IsDetected(InstallerType type, string arguments, bool expected)
    {
        Assert.Equal(expected, InstallerNames.ContainsPathOverride(arguments, type));
    }

    [Theory]
    [InlineData("D:")]
    [InlineData(@"D:\")]
    [InlineData("d:")]
    public void DriveRoot_NormalizesAllCommonForms(string input)
    {
        Assert.Equal(@"D:\", DriveRedirect.Core.Rules.WindowsPathTools.NormalizeDriveRoot(input));
    }
}
