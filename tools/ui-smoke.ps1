param([string]$OutDir = "artifacts\screenshots")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class UiSmoke3 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindLargestVisibleWindow(int processId) {
    IntPtr best = IntPtr.Zero;
    long bestArea = 0;
    EnumWindows((hWnd, lParam) => {
      uint owner;
      GetWindowThreadProcessId(hWnd, out owner);
      if (owner != (uint)processId || !IsWindowVisible(hWnd))
        return true;
      RECT rect;
      if (!GetWindowRect(hWnd, out rect))
        return true;
      long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
      if (area > bestArea) {
        bestArea = area;
        best = hWnd;
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
"@
$thread = [UiSmoke3]::SetThreadDpiAwarenessContext([IntPtr](-4))
$procs = @(Get-Process DriveRedirect.Desktop -ErrorAction Stop)
$hwnd = [IntPtr]::Zero
$bestArea = 0
foreach ($proc in $procs) {
  $candidate = [UiSmoke3]::FindLargestVisibleWindow($proc.Id)
  if ($candidate -eq [IntPtr]::Zero) { continue }
  $candidateRect = New-Object UiSmoke3+RECT
  [UiSmoke3]::GetWindowRect($candidate, [ref]$candidateRect) | Out-Null
  $area = ($candidateRect.Right - $candidateRect.Left) * ($candidateRect.Bottom - $candidateRect.Top)
  if ($area -gt $bestArea) {
    $bestArea = $area
    $hwnd = $candidate
  }
}
if ($hwnd -eq 0) { throw "main window not found" }
[UiSmoke3]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 300
$rect = New-Object UiSmoke3+RECT
[UiSmoke3]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outPath = (Resolve-Path $OutDir).Path
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Capture([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
  $path = Join-Path $outPath ($name + '.png')
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output $path
}

function Find-NavItems {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ListItem)
  return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Select-Nav([int]$index) {
  $items = Find-NavItems
  if ($items.Count -le $index) { throw "nav item missing: $index (count=$($items.Count))" }
  $pattern = $items.Item($index).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $pattern.Select()
  Start-Sleep -Milliseconds 1000
}

$names = @('01-dashboard','02-live','03-history','04-rules','05-compatibility','06-settings')
for ($i = 0; $i -lt $names.Count; $i++) {
  Select-Nav $i
  Capture $names[$i] | Write-Output
}

Select-Nav 0
$themeCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeButton')
$themeButton = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $themeCond)
if ($themeButton) {
  $invoke = $themeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $invoke.Invoke()
  Start-Sleep -Milliseconds 1200
  Capture '07-dashboard-dark' | Write-Output
  $invoke.Invoke()
} else {
  Write-Output 'theme button not found'
}
[UiSmoke3]::SetThreadDpiAwarenessContext($thread) | Out-Null
