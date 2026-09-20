using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Bitchat.Windows.App;

/// <summary>Dark title bar + caption/text colors (Windows 10 1809+, caption color on Windows 11).</summary>
public static class DarkChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Caption background in COLORREF (0x00BBGGRR).</summary>
    private static int CaptionColor = 0x00221F1E; // #1E1F22
    private static int TextColor = 0x00EDEAE8;    // #E8EAED

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref CaptionColor, 4);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref TextColor, 4);
        }
        catch
        {
            // older systems without DWM color attributes — dark mode attr still applied above
        }
    }
}
