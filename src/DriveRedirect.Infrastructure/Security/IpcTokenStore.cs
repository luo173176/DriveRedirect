using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DriveRedirect.Infrastructure.Configuration;

namespace DriveRedirect.Infrastructure.Security;

public sealed class IpcTokenStore
{
    private readonly AppPaths _paths;
    private string? _cached;

    public IpcTokenStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> GetOrCreateAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cached))
            return _cached;

        Directory.CreateDirectory(_paths.DataDirectory);
        if (!File.Exists(_paths.IpcTokenPath))
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            await File.WriteAllTextAsync(_paths.IpcTokenPath, token, new UTF8Encoding(false), ct);
            TryRestrictFilePermissions(_paths.IpcTokenPath);
            _cached = token;
            return token;
        }

        _cached = (await File.ReadAllTextAsync(_paths.IpcTokenPath, ct)).Trim();
        return _cached;
    }

    public bool Validate(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided) || string.IsNullOrWhiteSpace(_cached))
            return false;
        var expected = Encoding.UTF8.GetBytes(_cached);
        var actual = Encoding.UTF8.GetBytes(provided);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void TryRestrictFilePermissions(string path)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, AccessControlType.Allow));
            var info = new FileInfo(path);
            info.SetAccessControl(security);
        }
        catch
        {
            // A non-elevated development run may not be able to set an ACL.
        }
    }
}
