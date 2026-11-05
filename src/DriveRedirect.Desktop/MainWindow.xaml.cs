using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DriveRedirect.Desktop.ViewModels;

namespace DriveRedirect.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TranslateTransform _pageTranslate = new(0, 8);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => FitToMonitor();
        Loaded += (_, _) => FitToMonitor();
        PageHost.RenderTransform = _pageTranslate;
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Loaded += (_, _) => UpdateWindowStateVisuals();
    }

    private void FitToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;

        var scale = GetMonitorScale(monitor, handle);
        var workWidth = (info.rcWork.Right - info.rcWork.Left) / scale;
        var workHeight = (info.rcWork.Bottom - info.rcWork.Top) / scale;
        if (workWidth <= 0 || workHeight <= 0)
            return;

        Width = Math.Min(Width, Math.Max(MinWidth, workWidth - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, workHeight - 24));
        Left = info.rcWork.Left / scale + Math.Max(0, (workWidth - Width) / 2);
        Top = info.rcWork.Top / scale + Math.Max(0, (workHeight - Height) / 2);
    }

    private static double GetMonitorScale(IntPtr monitor, IntPtr window)
    {
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            return Math.Max(1.0, dpiX / 96.0);
        var windowDpi = GetDpiForWindow(window);
        return Math.Max(1.0, windowDpi / 96.0);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            _pageTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        });
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowStateVisuals()
    {
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }
}
