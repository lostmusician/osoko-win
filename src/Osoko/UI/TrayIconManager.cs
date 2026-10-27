using System.Drawing;
using Drawing = System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Osoko.UI;

/// <summary>Loads the application icon (also used for the notification area).</summary>
internal static class IconFactory
{
    private static readonly Uri IconUri = new("pack://application:,,,/Osoko;component/Assets/osoko.ico", UriKind.Absolute);

    /// <summary>Creates a tray-sized icon from the embedded multi-resolution .ico.</summary>
    internal static Icon CreateTrayIcon(int size = 32)
    {
        try
        {
            var resource = Application.GetResourceStream(IconUri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, size, size);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UriFormatException)
        {
            Core.Log.Warn($"Could not load the application icon: {ex.Message}");
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}

/// <summary>Callbacks the tray menu invokes.</summary>
internal sealed record TrayActions(
    Action ShowShelf,
    Action OpenSettings,
    Action ClearStack,
    Action OpenScratchFolder,
    Action Exit);

/// <summary>
/// Notification-area presence built on <see cref="Forms.NotifyIcon"/>. The icon is the
/// only reason Osoko references WinForms; everything else is WPF / Win32.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private bool _disposed;

    internal TrayIconManager(TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Color.FromArgb(28, 28, 36),
            ForeColor = Color.FromArgb(240, 240, 248),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable()),
        };

        _menu.Items.Add(CreateItem("Open Osoko shelf", actions.ShowShelf, bold: true));
        _menu.Items.Add(CreateItem("Settings…", actions.OpenSettings));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("Clear shelf", actions.ClearStack));
        _menu.Items.Add(CreateItem("Open scratch folder", actions.OpenScratchFolder));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("Exit Osoko", actions.Exit));

        _icon = IconFactory.CreateTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Osoko — screenshot shelf",
            Visible = false,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => SafeInvoke(actions.ShowShelf);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                SafeInvoke(actions.ShowShelf);
            }
        };
    }

    internal bool Visible
    {
        get => _notifyIcon.Visible;
        set
        {
            if (_disposed)
            {
                return;
            }

            _notifyIcon.Visible = value;
        }
    }

    internal void UpdateTooltip(string text)
    {
        if (_disposed)
        {
            return;
        }

        // NotifyIcon.Text is capped at 63 characters.
        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    internal void ShowNotification(string title, string message, bool isError = false)
    {
        if (_disposed || !_notifyIcon.Visible)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = isError ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private static Forms.ToolStripMenuItem CreateItem(string text, Action action, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Font = new Font(new FontFamily("Segoe UI"), 9f, bold ? Drawing.FontStyle.Bold : Drawing.FontStyle.Regular),
            Padding = new Forms.Padding(4, 3, 4, 3),
        };

        item.Click += (_, _) => SafeInvoke(action);
        return item;
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Core.Log.Error("Tray action failed", ex);
        }
    }

    /// <summary>Dark palette for the tray context menu.</summary>
    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(28, 28, 36);
        private static readonly Color Hover = Color.FromArgb(124, 108, 247);

        public override Color ToolStripDropDownBackground => Background;

        public override Color ImageMarginGradientBegin => Background;

        public override Color ImageMarginGradientMiddle => Background;

        public override Color ImageMarginGradientEnd => Background;

        public override Color MenuBorder => Color.FromArgb(58, 58, 74);

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color SeparatorDark => Color.FromArgb(48, 48, 62);

        public override Color SeparatorLight => Color.FromArgb(48, 48, 62);
    }
}
