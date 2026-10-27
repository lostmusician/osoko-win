using System.Windows.Media;

namespace Osoko.Interop;

/// <summary>
/// Helpers around <c>DwmSetWindowAttribute</c>. Every call is best effort: on older
/// builds (or when composition is disabled) the attribute simply does not stick and
/// the WPF chrome remains in charge.
/// </summary>
internal static class DwmHelper
{
    /// <summary>Windows 11 22000 (21H2) is the first build with rounded corners + Mica.</summary>
    private static readonly bool IsWindows11OrGreater = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Windows 11 22621 (22H2) is the first build with <c>DWMWA_SYSTEMBACKDROP_TYPE</c>.</summary>
    private static readonly bool SupportsSystemBackdrop = Environment.OSVersion.Version.Build >= 22621;

    internal static bool Windows11 => IsWindows11OrGreater;

    /// <summary>Applies the DWM rounded-corner preference (Windows 11 only).</summary>
    internal static bool EnableRoundedCorners(
        IntPtr hwnd,
        WindowStyles.CornerPreference preference = WindowStyles.CornerPreference.Round)
    {
        if (!IsWindows11OrGreater || hwnd == IntPtr.Zero)
        {
            return false;
        }

        return TrySetIntAttribute(
            hwnd,
            WindowStyles.DWMWA_WINDOW_CORNER_PREFERENCE,
            (int)preference);
    }

    /// <summary>Requests a Mica / Acrylic backdrop (Windows 11 22H2+).</summary>
    internal static bool TrySetBackdrop(IntPtr hwnd, WindowStyles.SystemBackdrop backdrop)
    {
        if (!SupportsSystemBackdrop || hwnd == IntPtr.Zero)
        {
            return false;
        }

        return TrySetIntAttribute(hwnd, WindowStyles.DWMWA_SYSTEMBACKDROP_TYPE, (int)backdrop);
    }

    /// <summary>Enables the immersive dark title bar for the window frame.</summary>
    internal static bool SetDarkMode(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var value = enabled ? 1 : 0;
        if (TrySetIntAttribute(hwnd, WindowStyles.DWMWA_USE_IMMERSIVE_DARK_MODE, value))
        {
            return true;
        }

        // Windows 10 1809-1909 used attribute 19.
        return TrySetIntAttribute(hwnd, WindowStyles.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, value);
    }

    /// <summary>Tints the 1px DWM window border (Windows 11 only).</summary>
    internal static bool SetBorderColor(IntPtr hwnd, Color color)
    {
        if (!IsWindows11OrGreater || hwnd == IntPtr.Zero)
        {
            return false;
        }

        // DWM expects a COLORREF (0x00BBGGRR). 0xFFFFFFFF means "no border".
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        return TrySetIntAttribute(hwnd, WindowStyles.DWMWA_BORDER_COLOR, colorRef);
    }

    /// <summary>
    /// Hides the window from screenshot / capture APIs. Falls back to
    /// <c>WDA_MONITOR</c> (renders black) when <c>WDA_EXCLUDEFROMCAPTURE</c>
    /// is unavailable (Windows 10 before 2004).
    /// </summary>
    /// <returns>The affinity that was successfully applied, or <c>WDA_NONE</c>.</returns>
    internal static uint ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return WindowStyles.WDA_NONE;
        }

        if (NativeMethods.SetWindowDisplayAffinity(hwnd, WindowStyles.WDA_EXCLUDEFROMCAPTURE))
        {
            return WindowStyles.WDA_EXCLUDEFROMCAPTURE;
        }

        if (NativeMethods.SetWindowDisplayAffinity(hwnd, WindowStyles.WDA_MONITOR))
        {
            return WindowStyles.WDA_MONITOR;
        }

        return WindowStyles.WDA_NONE;
    }

    /// <summary>
    /// Turns a top-level window into a "ghost": never activated, hidden from Alt+Tab and
    /// the taskbar, and invisible to screen capture.
    /// </summary>
    internal static void MakeGhostWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AddExtendedStyle(
            hwnd,
            WindowStyles.WS_EX_NOACTIVATE | WindowStyles.WS_EX_TOOLWINDOW);

        // Push the new extended styles to the window manager without moving or resizing.
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        ExcludeFromCapture(hwnd);
        SetDarkMode(hwnd, enabled: true);
    }

    private static bool TrySetIntAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
