using DriveRedirect.Core.Installer;

namespace DriveRedirect.Tests;

public sealed class LaunchCandidateDetectorTests
{
    [Theory]
    [InlineData("AcmeSetup.exe", true)]
    [InlineData("AcmeInstaller.exe", true)]
    [InlineData("AcmeWebInstall.exe", true)]
    [InlineData("AcmeUpdate.exe", false)]
    [InlineData("unins000.exe", false)]
    [InlineData("AcmeUninstall.exe", false)]
    [InlineData("notepad.exe", false)]
    public void NameBasedDetection_FiltersMaintenancePrograms(string fileName, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        try
        {
            Assert.Equal(expected, LaunchCandidateDetector.IsCandidate(path, out _));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData(".msi", true)]
    [InlineData(".msix", true)]
    [InlineData(".appx", true)]
    public void PackageExtensions_AreCandidates(string extension, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "package" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00]);
        try
        {
            Assert.Equal(expected, LaunchCandidateDetector.IsCandidate(path, out _));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
