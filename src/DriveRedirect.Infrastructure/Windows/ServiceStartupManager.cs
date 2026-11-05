using System.Runtime.InteropServices;

namespace DriveRedirect.Infrastructure.Windows;

public static class ServiceStartupManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDemandStart = 0x00000003;

    public static (bool Success, string? Error) SetAutomaticStart(string serviceName, bool automatic)
    {
        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
                return (false, "无法连接服务控制管理器：" + Marshal.GetLastWin32Error());
            service = OpenService(manager, serviceName, ServiceChangeConfig);
            if (service == IntPtr.Zero)
                return (false, "无法打开 DriveRedirect Service：" + Marshal.GetLastWin32Error());

            var startType = automatic ? ServiceAutoStart : ServiceDemandStart;
            var changed = ChangeServiceConfig(
                service,
                ServiceNoChange,
                startType,
                ServiceNoChange,
                null,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                null);
            return changed
                ? (true, null)
                : (false, "修改服务启动类型失败：" + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (service != IntPtr.Zero)
                CloseServiceHandle(service);
            if (manager != IntPtr.Zero)
                CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
