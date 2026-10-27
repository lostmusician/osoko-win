using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Osoko.Core;
using Osoko.Core.Services;
using Osoko.Interop;
using Osoko.UI.Controls;

namespace Osoko.UI;

/// <summary>
/// The floating, non-activating shelf.
/// <para>
/// It is a borderless, transparent, topmost window that never appears in Alt+Tab or on
/// the taskbar (<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>) and is excluded from screen
/// capture (<c>WDA_EXCLUDEFROMCAPTURE</c>) so screenshots never contain the shelf itself.
/// It hugs the bottom-right corner of the active monitor, collapses to the edge grab
/// handle after a configurable idle period and peeks back in when the pointer touches
/// the screen edge.
/// </para>
/// </summary>
public partial class FloatingShelfWindow : Window
{
    private const double ShelfSlideDistance = 352;
    private const double CardSlotHeight = 160; // 150px card + 10px gap
    private const double ChromeHeight = 128;
    private const double RightMargin = 4;
    private const double BottomMargin = 12;
    private const double MinShelfHeight = 200;

    private readonly ScreenshotManager _manager;
    private readonly OsokoSettings _settings;
    private readonly AudioService _audio;
    private readonly StackCollapseState _collapseState;
    private readonly DispatcherTimer _toastTimer;
    private bool _isDragging;
    private bool _allowClose;
    private bool _suspended;
    private uint _displayAffinity;
    private bool _displayEventsHooked;

    public FloatingShelfWindow(ScreenshotManager manager, OsokoSettings settings, AudioService audio)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));

        InitializeComponent();

        DataContext = _manager;

        _collapseState = new StackCollapseState(settings.AutoCollapseEnabled, settings.AutoCollapseDelaySeconds);
        _collapseState.StateChanged += OnCollapseStateChanged;
        _collapseState.EdgeHoverDetected += OnEdgeHoverDetected;
        _collapseState.EdgeHoverEnded += OnEdgeHoverEnded;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.7),
        };
        _toastTimer.Tick += OnToastTimerTick;

        ToastService.Requested += OnToastRequested;
        _manager.StackChanged += OnStackChanged;

        HotkeyHint.Visibility = settings.EnableGlobalHotkey ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Raised when the user clicks the gear button; the app opens the settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Shows the shelf (and expands it unless <paramref name="expand"/> is false).</summary>
    public void ShowShelf(bool expand = true)
    {
        if (_suspended)
        {
            return;
        }

        UpdateShelfSizeAndPosition();

        if (!IsVisible)
        {
            Show();
        }

        if (expand)
        {
            _collapseState.Expand();
        }
        else
        {
            _collapseState.EnterHoverPeek();
        }

        _collapseState.StartEdgePolling();
        ApplyStateVisual(animate: true);
        BringToFront();
    }

    /// <summary>
    /// Hides the shelf and ignores capture/tray requests until <see cref="Resume"/> is
    /// called. Used while a modal settings dialog is open so the always-on-top shelf can
    /// never cover it.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        HideShelf();
    }

    /// <summary>Re-enables the shelf after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        if (_manager.HasItems)
        {
            ShowShelf(expand: false);
        }
    }

    /// <summary>Hides the shelf entirely (used when the stack becomes empty).</summary>
    public void HideShelf()
    {
        _collapseState.StopEdgePolling();
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Re-applies settings that affect layout or timing.</summary>
    public void ApplySettings()
    {
        _collapseState.ApplyTiming(_settings.AutoCollapseEnabled, _settings.AutoCollapseDelaySeconds);
        HotkeyHint.Visibility = _settings.EnableGlobalHotkey ? Visibility.Visible : Visibility.Collapsed;
        UpdateShelfSizeAndPosition();
    }

    /// <summary>Allows the window to close during application shutdown.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        _collapseState.Dispose();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // Ghost behaviour: never activated, never in Alt+Tab, never captured.
        DwmHelper.MakeGhostWindow(handle);
        _displayAffinity = DwmHelper.ExcludeFromCapture(handle);

        // We draw our own rounded surface, so disable the DWM rounding for this window.
        DwmHelper.EnableRoundedCorners(handle, WindowStyles.CornerPreference.DoNotRound);

        // Layered windows sometimes reject the affinity until the first paint.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_displayAffinity == WindowStyles.WDA_NONE)
            {
                _displayAffinity = DwmHelper.ExcludeFromCapture(handle);
            }
        }));
    }

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateShelfSizeAndPosition();
        ApplyStateVisual(animate: false);
        _collapseState.StartEdgePolling();

        if (!_displayEventsHooked)
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displayEventsHooked = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ToastService.Requested -= OnToastRequested;
        _manager.StackChanged -= OnStackChanged;
        _collapseState.StateChanged -= OnCollapseStateChanged;
        _collapseState.EdgeHoverDetected -= OnEdgeHoverDetected;
        _collapseState.EdgeHoverEnded -= OnEdgeHoverEnded;
        _collapseState.Dispose();

        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTimerTick;

        if (_displayEventsHooked)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displayEventsHooked = false;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        // Closing the shelf (Alt+F4, WM_CLOSE) should only hide it.
        e.Cancel = true;
        HideShelf();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateShelfSizeAndPosition));
    }

    // ---------------------------------------------------------------------
    // Stack updates
    // ---------------------------------------------------------------------

    private void OnStackChanged(object? sender, StackChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnStackChanged(sender, e)));
            return;
        }

        UpdateShelfSizeAndPosition();

        if (_manager.HasItems)
        {
            if (!IsVisible)
            {
                ShowShelf(expand: true);
            }
        }
        else
        {
            HideShelf();
        }
    }

    // ---------------------------------------------------------------------
    // Layout & positioning
    // ---------------------------------------------------------------------

    private void UpdateShelfSizeAndPosition()
    {
        if (!IsInitialized)
        {
            return;
        }

        var itemCount = _manager.Items.Count;
        var desiredHeight = ChromeHeight + Math.Max(1, itemCount) * CardSlotHeight - 10;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleY = dpi.DpiScaleY <= 0 ? 1d : dpi.DpiScaleY;
        var work = ScreenHelper.GetActiveWorkArea();
        var maxHeight = Math.Max(MinShelfHeight, work.Height * 0.84 / scaleY);

        Height = Math.Clamp(desiredHeight, MinShelfHeight, maxHeight);
        Reposition();
    }

    private void Reposition()
    {
        if (!IsInitialized)
        {
            return;
        }

        var work = ScreenHelper.GetActiveWorkArea();
        var primaryScale = ScreenHelper.GetPrimaryScale();
        if (primaryScale <= 0)
        {
            primaryScale = 1d;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var windowWidth = (ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX;
        var windowHeight = Height * dpi.DpiScaleY;

        var physicalLeft = work.Right - windowWidth - (RightMargin * primaryScale);
        var physicalTop = work.Bottom - windowHeight - (BottomMargin * primaryScale);

        physicalLeft = Math.Max(work.Left, Math.Min(physicalLeft, work.Right - windowWidth));
        physicalTop = Math.Max(work.Top, Math.Min(physicalTop, work.Bottom - windowHeight));

        Left = physicalLeft / primaryScale;
        Top = physicalTop / primaryScale;

        // Area that reveals a collapsed shelf: the right-hand slice of the window plus a
        // thin strip along the screen edge (so the peek works even with the 4px inset).
        var peekWidth = Math.Min(windowWidth, 160 * primaryScale);
        _collapseState.ShelfBoundsInScreenPixels =
            new Rect(work.Right - RightMargin * primaryScale - peekWidth, physicalTop, peekWidth, windowHeight);
        _collapseState.EdgeActivationBandInScreenPixels =
            new Rect(work.Right - 10 * primaryScale, physicalTop, 10 * primaryScale, windowHeight);
    }

    private void BringToFront()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW);
    }

    // ---------------------------------------------------------------------
    // Collapse / expand
    // ---------------------------------------------------------------------

    private void OnCollapseStateChanged(object? sender, ShelfStateChangedEventArgs e) => ApplyStateVisual(animate: true);

    private void OnEdgeHoverDetected(object? sender, EventArgs e)
    {
        if (!_manager.HasItems || _isDragging)
        {
            return;
        }

        ShowShelf(expand: false);
    }

    private void OnEdgeHoverEnded(object? sender, EventArgs e)
    {
        if (_collapseState.State == ShelfState.HoverPeek && !_isDragging)
        {
            _collapseState.Collapse();
            ApplyStateVisual(animate: true);
        }
    }

    private void ApplyStateVisual(bool animate)
    {
        if (!IsInitialized)
        {
            return;
        }

        var expanded = _collapseState.State != ShelfState.Collapsed;
        Handle.IsExpanded = expanded;
        ShelfPanel.IsHitTestVisible = expanded;

        var targetX = expanded ? 0d : ShelfSlideDistance;
        var targetOpacity = expanded ? 1d : 0d;

        if (!animate || !IsLoaded)
        {
            ShelfTransform.X = targetX;
            ShelfPanel.Opacity = targetOpacity;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ShelfTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(expanded ? 240 : 300))
            {
                EasingFunction = ease,
            });

        ShelfPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(expanded ? 170 : 220))
            {
                EasingFunction = ease,
            });
    }

    private void OnHandleToggle(object? sender, EventArgs e)
    {
        if (!_manager.HasItems)
        {
            HideShelf();
            return;
        }

        if (_collapseState.State == ShelfState.Expanded)
        {
            _collapseState.Collapse();
        }
        else
        {
            ShowShelf(expand: true);
        }

        ApplyStateVisual(animate: true);
    }

    private void OnHandleMouseEnter(object sender, MouseEventArgs e)
    {
        if (_manager.HasItems)
        {
            _collapseState.Expand();
            ApplyStateVisual(animate: true);
        }
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (_manager.HasItems)
        {
            _collapseState.Expand();
            ApplyStateVisual(animate: true);
        }
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        _collapseState.NotifyPointerLeftShelf();
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e) => _collapseState.NotifyPointerActivity();

    private void OnWindowMouseMove(object sender, MouseEventArgs e) => _collapseState.NotifyPointerActivity();

    // ---------------------------------------------------------------------
    // Cards
    // ---------------------------------------------------------------------

    private async void OnCardActionRequested(object sender, CardActionEventArgs e)
    {
        try
        {
            switch (e.Action)
            {
                case CardAction.CopyImage:
                    if (_manager.CopyImage(e.Item.Id))
                    {
                        _audio.Play(AudioCue.Copy);
                        ToastService.Show("Image Copied ✓");
                    }
                    else
                    {
                        ToastService.Show("Could not copy image");
                    }

                    break;

                case CardAction.CopyText:
                {
                    if (e.Item.IsRecognizing)
                    {
                        break;
                    }

                    _collapseState.NotifyPointerActivity();
                    var text = await _manager.CopyOcrTextAsync(e.Item.Id);
                    if (string.IsNullOrEmpty(text))
                    {
                        ToastService.Show("No text found");
                    }
                    else
                    {
                        _audio.Play(AudioCue.Copy);
                        ToastService.Show("Text Copied ✓");
                    }

                    break;
                }

                case CardAction.TogglePin:
                    _manager.PinScreenshot(e.Item.Id);
                    ToastService.Show(e.Item.IsPinned ? "Pinned ✓" : "Unpinned");
                    break;

                case CardAction.Save:
                {
                    _collapseState.NotifyPointerActivity();
                    var savedPath = _manager.Save(e.Item.Id);
                    ToastService.Show(savedPath is null ? "Save cancelled" : "Saved ✓");
                    break;
                }

                case CardAction.Delete:
                    if (_manager.RemoveScreenshot(e.Item.Id))
                    {
                        _audio.Play(AudioCue.Delete);
                    }

                    break;

                case CardAction.DragCompleted:
                    if (_settings.ConsumeOnDrop && e.Effect != DragDropEffects.None)
                    {
                        _manager.ConsumeDrop(e.Item.Id);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Card action '{e.Action}' failed", ex);
            ToastService.Show("Something went wrong");
        }
        finally
        {
            _collapseState.NotifyPointerActivity();
        }
    }

    private void OnCardDragStateChanged(object sender, CardDragStateEventArgs e)
    {
        _isDragging = e.IsDragging;
        if (!_isDragging)
        {
            _collapseState.NotifyPointerActivity();
        }
    }

    // ---------------------------------------------------------------------
    // Header buttons
    // ---------------------------------------------------------------------

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var removed = _manager.ClearAll();
        if (removed > 0)
        {
            _audio.Play(AudioCue.Delete);
            ToastService.Show(removed == 1 ? "Shelf cleared" : $"{removed} captures cleared");
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // Toasts
    // ---------------------------------------------------------------------

    private void OnToastRequested(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnToastRequested(message)));
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        ToastText.Text = message;
        ToastBanner.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        ToastBanner.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
    }
}
