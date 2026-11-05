param([long]$Hwnd, [int]$ScreenX, [int]$ScreenY, [string]$OutFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MsgClick {
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
}
"@
$old=[MsgClick]::SetThreadDpiAwarenessContext([IntPtr](-4))
$h=[IntPtr]$Hwnd
[MsgClick]::SetForegroundWindow($h) | Out-Null
$p=New-Object MsgClick+POINT; $p.X=$ScreenX; $p.Y=$ScreenY
[MsgClick]::ScreenToClient($h,[ref]$p) | Out-Null
$l=[IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xffff))
[MsgClick]::PostMessage($h,0x0200,[IntPtr]0,$l) | Out-Null
[MsgClick]::PostMessage($h,0x0201,[IntPtr]1,$l) | Out-Null
Start-Sleep -Milliseconds 80
[MsgClick]::PostMessage($h,0x0202,[IntPtr]0,$l) | Out-Null
Start-Sleep -Milliseconds 900
$r=New-Object MsgClick+RECT; [MsgClick]::GetWindowRect($h,[ref]$r) | Out-Null
Add-Type -AssemblyName System.Drawing
$bmp=New-Object System.Drawing.Bitmap ($r.Right-$r.Left),($r.Bottom-$r.Top)
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left,$r.Top,0,0,$bmp.Size)
$bmp.Save($OutFile,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("client=" + $p.X + "," + $p.Y)
[MsgClick]::SetThreadDpiAwarenessContext($old) | Out-Null
