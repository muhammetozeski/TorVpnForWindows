#Requires -Version 7.0
<#
.SYNOPSIS
    Saves a PNG of a running program's main window.

.DESCRIPTION
    Used to produce the screenshot in the README without hand-cropping.

    It asks the window to render itself with PrintWindow rather than copying that region of the
    screen. Copying the screen captures whatever happens to be in front, which produced a picture
    of an unrelated window every time something else had focus.

.PARAMETER ProcessName
    Process name without the extension, for example TorVpnForWindows.

.PARAMETER OutputPath
    Where to write the PNG.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ProcessName,
    [Parameter(Mandatory)] [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not ('WindowCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out Rect value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    // Excludes the invisible resize border that GetWindowRect includes on Windows 10 and later.
    public const int DwmwaExtendedFrameBounds = 9;

    // Renders the whole window including composited content, which a plain PrintWindow misses on
    // hardware accelerated surfaces such as WPF.
    public const uint PwRenderFullContent = 2;
}
'@
}

$process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $process) {
    throw "No running process named '$ProcessName' has a visible window."
}

$handle = $process.MainWindowHandle
[void][WindowCapture]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 600

# GetWindowRect is the right size for PrintWindow: the DWM frame bounds exclude the resize border
# that PrintWindow still draws, which would clip the right and bottom edges.
$rect = New-Object WindowCapture+Rect
if (-not [WindowCapture]::GetWindowRect($handle, [ref]$rect)) {
    throw 'The window rectangle could not be read.'
}

$width  = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
    throw "The window rectangle is empty ($width x $height)."
}

$bitmap   = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $hdc = $graphics.GetHdc()
    try {
        $printed = [WindowCapture]::PrintWindow($handle, $hdc, [WindowCapture]::PwRenderFullContent)
    } finally {
        $graphics.ReleaseHdc($hdc)
    }

    if (-not $printed) {
        # PrintWindow refused; fall back to reading the screen, which needs the window in front.
        Write-Host 'PrintWindow failed, copying the screen instead'
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    }

    New-Item -ItemType Directory -Force (Split-Path $OutputPath -Parent) | Out-Null
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "saved $OutputPath ($width x $height)"
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
