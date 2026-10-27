namespace Osoko.Interop;

/// <summary>Physical pixel rectangle of a monitor's work area (taskbar excluded).</summary>
internal readonly record struct MonitorWorkArea(int Left, int Top, int Right, int Bottom, uint DpiX, uint DpiY)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
    internal double ScaleX => DpiX <= 0 ? 1.0 : DpiX / 96.0;
    internal double ScaleY => DpiY <= 0 ? 1.0 : DpiY / 96.0;
}

/// <summary>
/// Monitor geometry helpers. Coordinates returned here are physical pixels; the
/// floating shelf converts them into WPF device-independent units before positioning.
/// </summary>
internal static class ScreenHelper
{
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Work area of the monitor that currently contains the mouse cursor.</summary>
    internal static MonitorWorkArea GetActiveWorkArea()
    {
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                return FromMonitor(monitor);
            }
        }

        return GetPrimaryWorkArea();
    }

    /// <summary>Work area of the primary monitor.</summary>
    internal static MonitorWorkArea GetPrimaryWorkArea()
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT(0, 0),
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        return monitor == IntPtr.Zero ? FallbackWorkArea() : FromMonitor(monitor);
    }

    /// <summary>
    /// DPI scale of the primary monitor. WPF keeps window coordinates in the primary
    /// monitor's DIP space even under PerMonitorV2, so cross-monitor math needs it.
    /// </summary>
    internal static double GetPrimaryScale()
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT(0, 0),
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        var (dpiX, _) = GetMonitorDpi(monitor);
        return dpiX / 96.0;
    }

    /// <summary>Physical bounds of the monitor containing the given screen point.</summary>
    internal static MonitorWorkArea GetWorkAreaForPoint(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT(x, y),
            NativeMethods.MONITOR_DEFAULTTONEAREST);

        return monitor == IntPtr.Zero ? GetPrimaryWorkArea() : FromMonitor(monitor);
    }

    private static MonitorWorkArea FromMonitor(IntPtr monitor)
    {
        var info = new NativeMethods.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return FallbackWorkArea();
        }

        var dpi = GetMonitorDpi(monitor);
        return new MonitorWorkArea(
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom,
            dpi.DpiX,
            dpi.DpiY);
    }

    private static (uint DpiX, uint DpiY) GetMonitorDpi(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0 &&
            dpiX > 0 && dpiY > 0)
        {
            return (dpiX, dpiY);
        }

        return (96, 96);
    }

    private static MonitorWorkArea FallbackWorkArea()
    {
        // Extremely defensive: composition-less / service-session fallback.
        var width = 1920;
        var height = 1080;
        return new MonitorWorkArea(0, 0, width, height - 48, 96, 96);
    }
}
