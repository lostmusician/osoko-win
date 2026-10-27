using System.Windows.Interop;
using Osoko.Interop;

namespace Osoko.Core.Services;

/// <summary>
/// Optional global hotkey (default <c>Alt+Shift+S</c>) that opens the Windows Snipping
/// Tool via the <c>ms-screenclip:</c> protocol, so Osoko can be the single entry point
/// for captures without re-implementing a snipping overlay.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4F53; // "OS"
    private const uint VirtualKeyS = 0x53;
    private const string SnippingToolProtocol = "ms-screenclip:";

    private readonly Action _onHotkey;
    private HwndSource? _messageSource;
    private bool _registered;
    private bool _disposed;

    public HotkeyService(Action onHotkey)
    {
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));
    }

    public bool IsRegistered => _registered;

    /// <summary>Registers <c>Alt+Shift+S</c>. Returns false when another app owns the combination.</summary>
    public bool Register()
    {
        if (_disposed || _registered)
        {
            return _registered;
        }

        try
        {
            if (_messageSource is null)
            {
                var parameters = new HwndSourceParameters("Osoko.HotkeyWindow")
                {
                    ParentWindow = NativeMethods.HWND_MESSAGE,
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0,
                };

                _messageSource = new HwndSource(parameters);
                _messageSource.AddHook(WndProc);
            }

            _registered = NativeMethods.RegisterHotKey(
                _messageSource.Handle,
                HotkeyId,
                (uint)(HotkeyModifiers.Alt | HotkeyModifiers.Shift),
                VirtualKeyS);

            if (!_registered)
            {
                Log.Warn("Alt+Shift+S is already registered by another application.");
            }

            return _registered;
        }
        catch (Exception ex)
        {
            Log.Error("Global hotkey registration failed", ex);
            return false;
        }
    }

    public void Unregister()
    {
        if (!_registered || _messageSource is null)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_messageSource.Handle, HotkeyId);
        _registered = false;
    }

    /// <summary>Launches the Windows Snipping Tool capture overlay.</summary>
    public static void LaunchSnippingTool()
    {
        try
        {
            NativeMethods.ShellExecute(
                IntPtr.Zero,
                "open",
                SnippingToolProtocol,
                null,
                null,
                WindowStyles.SW_SHOWNORMAL);
        }
        catch (Exception ex)
        {
            Log.Error("Could not launch the Snipping Tool", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_messageSource is not null)
        {
            _messageSource.RemoveHook(WndProc);
            _messageSource.Dispose();
            _messageSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                _onHotkey();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey callback failed", ex);
            }
        }

        return IntPtr.Zero;
    }
}
