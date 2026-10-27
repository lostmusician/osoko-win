namespace Osoko.Interop;

/// <summary>
/// Win32 style bits, window-display affinities, DWM attributes and hotkey modifiers
/// used throughout Osoko. Values are documented so the intent survives refactors.
/// </summary>
internal static class WindowStyles
{
    // ---------------------------------------------------------------------
    // Extended window styles (GWL_EXSTYLE)
    // ---------------------------------------------------------------------

    /// <summary>The window never becomes active; clicks are handled without stealing focus.</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Keeps the window out of Alt+Tab and off the taskbar.</summary>
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Layered window (set by WPF when <c>AllowsTransparency</c> is used).</summary>
    internal const int WS_EX_LAYERED = 0x00080000;

    /// <summary>Prevents the window from receiving mouse input (used for click-through overlays).</summary>
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    // ---------------------------------------------------------------------
    // SetWindowDisplayAffinity values
    // ---------------------------------------------------------------------

    /// <summary>Capture is allowed (default).</summary>
    internal const uint WDA_NONE = 0x00000000;

    /// <summary>The window renders as black in capture APIs (Windows 7+).</summary>
    internal const uint WDA_MONITOR = 0x00000001;

    /// <summary>The window is completely removed from screenshots / screen recordings (Windows 10 2004+).</summary>
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // ---------------------------------------------------------------------
    // DWM window attributes
    // ---------------------------------------------------------------------

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_CAPTION_COLOR = 35;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>Windows 10 era fallback where corner preference was not yet defined.</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    // ---------------------------------------------------------------------
    // DWM_WINDOW_CORNER_PREFERENCE
    // ---------------------------------------------------------------------

    internal const int DWMWCP_DEFAULT = 0;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMWCP_ROUNDSMALL = 3;

    // ---------------------------------------------------------------------
    // DWM_SYSTEMBACKDROP_TYPE
    // ---------------------------------------------------------------------

    internal const int DWMSBT_AUTO = 0;
    internal const int DWMSBT_NONE = 1;

    /// <summary>Mica — samples the desktop wallpaper behind the window (Windows 11 22000+).</summary>
    internal const int DWMSBT_MAINWINDOW = 2;

    /// <summary>Acrylic — blurred, translucent background (Windows 11 22621+).</summary>
    internal const int DWMSBT_TRANSIENTWINDOW = 3;

    /// <summary>Mica Alt / tabbed backdrop.</summary>
    internal const int DWMSBT_TABBEDWINDOW = 4;

    // ---------------------------------------------------------------------
    // ShowWindow commands
    // ---------------------------------------------------------------------

    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;

    /// <summary>Mica / Acrylic backdrop choices surfaced to the UI layer.</summary>
    internal enum SystemBackdrop
    {
        None = DWMSBT_NONE,
        Mica = DWMSBT_MAINWINDOW,
        Acrylic = DWMSBT_TRANSIENTWINDOW,
        MicaAlt = DWMSBT_TABBEDWINDOW,
    }

    /// <summary>Corner rounding choices surfaced to the UI layer.</summary>
    internal enum CornerPreference
    {
        Default = DWMWCP_DEFAULT,
        DoNotRound = DWMWCP_DONOTROUND,
        Round = DWMWCP_ROUND,
        RoundSmall = DWMWCP_ROUNDSMALL,
    }
}

/// <summary>Modifier keys for <c>RegisterHotKey</c>.</summary>
[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}
