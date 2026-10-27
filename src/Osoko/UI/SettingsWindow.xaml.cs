using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Osoko.Core;
using Osoko.Core.Services;
using Osoko.Interop;

namespace Osoko.UI;

/// <summary>
/// Modal settings editor. It edits a *clone* of the live settings so Cancel can never
/// leave the app half-configured; the app copies the values back on OK.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly OsokoSettings _working;
    private readonly IFileDialogService _fileDialogs;

    public SettingsWindow(OsokoSettings workingCopy, IFileDialogService fileDialogs)
    {
        _working = workingCopy ?? throw new ArgumentNullException(nameof(workingCopy));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));

        InitializeComponent();
        DataContext = _working;
    }

    /// <summary>The edited settings instance (valid after <see cref="Window.ShowDialog"/> returns true).</summary>
    public OsokoSettings Settings => _working;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        DwmHelper.SetDarkMode(handle, enabled: true);
        DwmHelper.EnableRoundedCorners(handle, WindowStyles.CornerPreference.Round);
        DwmHelper.SetBorderColor(handle, Color.FromRgb(0x2E, 0x2E, 0x3C));

        // Mica only shows through when the window background is translucent; our root
        // border uses a 92% opaque brush so the effect stays subtle on Windows 11.
        DwmHelper.TrySetBackdrop(handle, WindowStyles.SystemBackdrop.Mica);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the button was released before the modal loop started.
        }
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _fileDialogs.PickFolder("Choose where Osoko saves screenshots", _working.SaveLocationPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            _working.SaveLocationPath = folder;
        }
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        // Copy over the current instance so every binding refreshes.
        _working.CopyFrom(OsokoSettings.CreateDefault());
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _working.Normalize();

        if (string.IsNullOrWhiteSpace(_working.SaveLocationPath))
        {
            _working.SaveLocationPath = AppPaths.DesktopDirectory;
        }

        CloseWithResult(true);
    }

    private void CloseWithResult(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Not shown modally (e.g. designer / tests).
            Close();
        }
    }
}
