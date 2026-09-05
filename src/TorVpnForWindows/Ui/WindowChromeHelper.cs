using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Ui;

/// <summary>
/// Paints the title bar to match the dark window content. Without this the window has a light title
/// bar sitting on top of a dark body whenever Windows is in light mode.
/// </summary>
public static partial class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == nint.Zero)
            {
                return;
            }

            var enabled = 1;

            // The attribute number changed during Windows 10; try the current one and fall back.
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            // Purely cosmetic; a light title bar is not worth failing startup over.
            Log.Error("Could not switch the title bar to dark", ex);
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
