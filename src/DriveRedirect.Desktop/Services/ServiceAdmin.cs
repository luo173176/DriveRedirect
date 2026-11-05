using System.ServiceProcess;

namespace DriveRedirect.Desktop.Services;

public sealed class ServiceStatusInfo
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public ServiceControllerStatus Status { get; init; }
    public string? Error { get; init; }
    public string Display => !IsInstalled
        ? "未安装"
        : IsRunning
            ? "运行中"
            : Status switch
            {
                ServiceControllerStatus.StartPending => "正在启动",
                ServiceControllerStatus.StopPending => "正在停止",
                ServiceControllerStatus.Paused => "已暂停",
                _ => "已停止"
            };
}

public sealed class ServiceAdmin
{
    private const string ServiceName = "DriveRedirect Service";

    public Task<ServiceStatusInfo> GetStatusAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            return new ServiceStatusInfo
            {
                IsInstalled = true,
                IsRunning = controller.Status == ServiceControllerStatus.Running,
                Status = controller.Status
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceStatusInfo { IsInstalled = false, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new ServiceStatusInfo { IsInstalled = true, Error = ex.Message };
        }
    }, ct);

    public Task<ServiceOperationResult> RestartAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return ServiceOperationResult.Ok("后台服务已重启。");
        }
        catch (Exception ex)
        {
            return ServiceOperationResult.Fail("重启服务失败：" + ex.Message);
        }
    }, ct);

    public Task<ServiceOperationResult> StartAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            return ServiceOperationResult.Ok("后台服务已启动。");
        }
        catch (Exception ex)
        {
            return ServiceOperationResult.Fail("启动服务失败：" + ex.Message);
        }
    }, ct);
}

public sealed class ServiceOperationResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    public static ServiceOperationResult Ok(string message) => new() { Success = true, Message = message };
    public static ServiceOperationResult Fail(string message) => new() { Success = false, Message = message };
}
