using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Osoko.Core;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace Osoko.UI.Controls;

/// <summary>UI-only commands a card can request in addition to manager actions.</summary>
public enum CardCommand
{
    /// <summary>Run <see cref="CardActionEventArgs.Kind"/> through the screenshot manager.</summary>
    ExecuteAction,

    /// <summary>An OLE drag finished; <see cref="CardActionEventArgs.Effect"/> carries the result.</summary>
    DragCompleted,

    /// <summary>Remove the card from the shelf.</summary>
    Remove,

    /// <summary>Clear every unpinned card from the shelf.</summary>
    ClearShelf,

    /// <summary>Open the settings window.</summary>
    OpenSettings,

    /// <summary>Reveal the capture in File Explorer.</summary>
    RevealInExplorer,
}

/// <summary>Payload for <see cref="ScreenshotCard.ActionRequested"/>.</summary>
public sealed class CardActionEventArgs : RoutedEventArgs
{
    public CardActionEventArgs(
        RoutedEvent routedEvent,
        CardCommand command,
        CardActionKind kind,
        ScreenshotItem item,
        DragDropEffects effect = DragDropEffects.None)
        : base(routedEvent)
    {
        Command = command;
        Kind = kind;
        Item = item;
        Effect = effect;
    }

    public CardActionEventArgs(RoutedEvent routedEvent, CardActionKind kind, ScreenshotItem item)
        : this(routedEvent, CardCommand.ExecuteAction, kind, item)
    {
    }

    public CardActionEventArgs(
        RoutedEvent routedEvent,
        CardActionKind kind,
        ScreenshotItem item,
        DragDropEffects effect)
        : this(routedEvent, CardCommand.ExecuteAction, kind, item, effect)
    {
    }

    public CardActionEventArgs(
        RoutedEvent routedEvent,
        CardCommand command,
        ScreenshotItem item,
        DragDropEffects effect = DragDropEffects.None)
        : this(routedEvent, command, CardActionKind.None, item, effect)
    {
    }

    /// <summary>What the host should do.</summary>
    public CardCommand Command { get; }

    /// <summary>Manager action to execute when <see cref="Command"/> is <see cref="CardCommand.ExecuteAction"/>.</summary>
    public CardActionKind Kind { get; }

    /// <summary>Alias for <see cref="Kind"/>.</summary>
    public CardActionKind ActionKind => Kind;

    /// <summary>Alias for <see cref="Kind"/>.</summary>
    public CardActionKind Action => Kind;

    /// <summary>True when this payload reports the result of an OLE drag.</summary>
    public bool IsDragCompleted => Command == CardCommand.DragCompleted;

    public ScreenshotItem Item { get; }

    public DragDropEffects Effect { get; }
}

/// <summary>Payload for <see cref="ScreenshotCard.DragStateChanged"/>.</summary>
public sealed class CardDragStateEventArgs : RoutedEventArgs
{
    public CardDragStateEventArgs(RoutedEvent routedEvent, bool isDragging)
        : base(routedEvent)
    {
        IsDragging = isDragging;
    }

    public bool IsDragging { get; }
}

/// <summary>
/// One capture on the shelf: a Fluent card with a high-DPI preview, QR indicator,
/// metadata strip, modular action slots, click gestures and a comprehensive context
/// menu. Dragging the card starts a multi-format OLE drag so it can be pasted into
/// any application.
/// </summary>
public partial class ScreenshotCard : UserControl
{
    /// <summary>Bubbling event carrying every toolbar, gesture, menu and drag request.</summary>
    public static readonly RoutedEvent ActionRequestedEvent = EventManager.RegisterRoutedEvent(
        "ActionRequested",
        RoutingStrategy.Bubble,
        typeof(EventHandler<CardActionEventArgs>),
        typeof(ScreenshotCard));

    /// <summary>Bubbling event fired when a drag starts and ends.</summary>
    public static readonly RoutedEvent DragStateChangedEvent = EventManager.RegisterRoutedEvent(
        "DragStateChanged",
        RoutingStrategy.Bubble,
        typeof(EventHandler<CardDragStateEventArgs>),
        typeof(ScreenshotCard));

    /// <summary>Action settings used by the modular toolbar, gestures and shortcuts.</summary>
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings),
        typeof(OsokoSettings),
        typeof(ScreenshotCard),
        new PropertyMetadata(null, OnSettingsPropertyChanged));

    private static readonly TimeSpan SingleClickDelay =
        TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);

    private readonly DispatcherTimer _singleClickTimer;
    private Point _dragOrigin;
    private bool _dragArmed;
    private bool _dragStarted;
    private bool _clickCandidate;
    private bool _doubleClickPending;
    private DoubleAnimation? _spinnerAnimation;
    private ScreenshotItem? _subscribedItem;
    private OsokoSettings? _subscribedSettings;

    public ScreenshotCard()
    {
        InitializeComponent();

        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = SingleClickDelay,
        };
        _singleClickTimer.Tick += OnSingleClickTimerTick;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<CardActionEventArgs> ActionRequested
    {
        add => AddHandler(ActionRequestedEvent, value);
        remove => RemoveHandler(ActionRequestedEvent, value);
    }

    public event EventHandler<CardDragStateEventArgs> DragStateChanged
    {
        add => AddHandler(DragStateChangedEvent, value);
        remove => RemoveHandler(DragStateChangedEvent, value);
    }

    /// <summary>Action settings supplied by the shelf's data template.</summary>
    public OsokoSettings? Settings
    {
        get => (OsokoSettings?)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    /// <summary>The item currently rendered by this card.</summary>
    public ScreenshotItem? Item => DataContext as ScreenshotItem;

    // ---------------------------------------------------------------------
    // Modular slots
    // ---------------------------------------------------------------------

    private void RefreshSlots()
    {
        ConfigureSlot(TopSlot1Button, Settings?.TopActionSlot1 ?? CardActionKind.None);
        ConfigureSlot(TopSlot2Button, Settings?.TopActionSlot2 ?? CardActionKind.None);
        ConfigureSlot(BottomSlot1Button, Settings?.BottomActionSlot1 ?? CardActionKind.None);
        ConfigureSlot(BottomSlot2Button, Settings?.BottomActionSlot2 ?? CardActionKind.None);
    }

    private void ConfigureSlot(Button button, CardActionKind action)
    {
        if (action == CardActionKind.None)
        {
            button.Visibility = Visibility.Collapsed;
            button.Tag = null;
            button.Content = null;
            button.ToolTip = null;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Tag = action;

        var isPinned = action == CardActionKind.Pin && Item?.IsPinned == true;
        button.Content = action.Glyph();
        button.ToolTip = action.Tooltip();

        var styleKey = isPinned ? "PinnedCardActionButtonStyle" : "CardActionButtonStyle";
        if (TryFindResource(styleKey) is Style style)
        {
            button.Style = style;
        }
    }

    private void OnSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CardActionKind action } && action != CardActionKind.None)
        {
            e.Handled = true;
            RequestAction(action);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardCommand.Remove);
    }

    private void OnQrPillClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        var item = Item;
        if (item is null || !item.HasDetectedBarcode)
        {
            return;
        }

        RequestAction(IsWebLink(item.DetectedBarcode)
            ? CardActionKind.OpenQrLink
            : CardActionKind.CopyQrLink);
    }

    private void UpdateQrPill()
    {
        var payload = Item?.DetectedBarcode;
        QrPillButton.ToolTip = string.IsNullOrWhiteSpace(payload)
            ? null
            : $"{payload}\nClick to open or copy · Right-click for more options";
    }

    // ---------------------------------------------------------------------
    // Context menu
    // ---------------------------------------------------------------------

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var item = Item;
        var hasBarcode = item?.HasDetectedBarcode == true;
        var isLink = hasBarcode && IsWebLink(item!.DetectedBarcode);

        ContextOpenQrItem.Visibility = isLink ? Visibility.Visible : Visibility.Collapsed;
        ContextCopyQrItem.Visibility = hasBarcode ? Visibility.Visible : Visibility.Collapsed;
        ContextQrSeparator.Visibility = hasBarcode ? Visibility.Visible : Visibility.Collapsed;
        ContextPinItem.Header = item?.IsPinned == true ? "Unpin" : "Pin to Shelf";

        if (ContextMenu is not null)
        {
            ContextMenu.DataContext = item;
        }
    }

    private void OnContextActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CardActionKind action } && action != CardActionKind.None)
        {
            e.Handled = true;
            RequestAction(action);
        }
    }

    private void OnContextCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CardCommand command })
        {
            e.Handled = true;
            Request(command);
        }
    }

    // ---------------------------------------------------------------------
    // Click gestures & hover shortcuts
    // ---------------------------------------------------------------------

    internal bool TryHandleHoverShortcut(KeyEventArgs e)
    {
        if (e.Handled || Settings?.EnableHoverShortcuts != true)
        {
            return false;
        }

        var request = ResolveShortcut(e.Key, Keyboard.Modifiers, Item);
        if (request is null)
        {
            return false;
        }

        e.Handled = true;
        Request(request.Value.Command, request.Value.Kind);
        return true;
    }

    internal void FocusForHoverShortcuts()
    {
        if (Settings?.EnableHoverShortcuts == true && Focusable)
        {
            Focus();
        }
    }

    private void OnCardPreviewKeyDown(object sender, KeyEventArgs e) => TryHandleHoverShortcut(e);

    private void OnSingleClickTimerTick(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();
        ExecuteGesture(doubleClick: false);
    }

    private void ExecuteGesture(bool doubleClick)
    {
        if (Item is null || Settings is null)
        {
            return;
        }

        var action = doubleClick ? Settings.DoubleClickAction : Settings.PrimaryClickAction;
        if (action != CardActionKind.None)
        {
            RequestAction(action);
        }
    }

    private static ShortcutRequest? ResolveShortcut(Key key, ModifierKeys modifiers, ScreenshotItem? item)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return null;
        }

        return key switch
        {
            Key.D1 or Key.NumPad1 => Execute(CardActionKind.CopyDownscaled),
            Key.D2 or Key.NumPad2 or Key.C => Execute(CardActionKind.CopyOriginal),
            Key.T => Execute(CardActionKind.CopyOcr),
            Key.R => Execute(CardActionKind.RedactSensitive),
            Key.L => Execute(IsWebLink(item?.DetectedBarcode)
                ? CardActionKind.OpenQrLink
                : CardActionKind.CopyQrLink),
            Key.P => Execute(CardActionKind.Pin),
            Key.S when (modifiers & ModifierKeys.Shift) != 0 => Execute(CardActionKind.SaveAs),
            Key.S => Execute(CardActionKind.SaveToFolder),
            Key.Space => Execute(CardActionKind.OpenInPreview),
            Key.Delete or Key.Back => new ShortcutRequest(CardCommand.Remove, CardActionKind.None),
            _ => null,
        };
    }

    private static ShortcutRequest Execute(CardActionKind kind) =>
        new(CardCommand.ExecuteAction, kind);

    private static bool IsWebLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // ---------------------------------------------------------------------
    // Hover visuals
    // ---------------------------------------------------------------------

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        TopActionBar.IsHitTestVisible = true;
        BottomSlotPanel.IsHitTestVisible = true;
        Animate(TopActionBar, OpacityProperty, 1d, 120);
        Animate(BottomSlotPanel, OpacityProperty, 1d, 120);
        FocusForHoverShortcuts();
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        TopActionBar.IsHitTestVisible = false;
        BottomSlotPanel.IsHitTestVisible = false;
        Animate(TopActionBar, OpacityProperty, 0d, 160);
        Animate(BottomSlotPanel, OpacityProperty, 0d, 160);

        // Drop the hover focus so shortcuts stop once the pointer leaves the card.
        if (IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
    }

    // ---------------------------------------------------------------------
    // Drag & drop (CF_HDROP + CF_DIB + CF_BITMAP + PNG)
    // ---------------------------------------------------------------------

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        if (IsWithinButton(source))
        {
            _dragArmed = false;
            _dragStarted = false;
            _clickCandidate = false;
            _doubleClickPending = false;
            _singleClickTimer.Stop();
            return;
        }

        _clickCandidate = IsWithinThumbnail(source);
        _doubleClickPending = e.ClickCount >= 2 && _clickCandidate;
        _dragStarted = false;

        if (_doubleClickPending || !_clickCandidate)
        {
            _singleClickTimer.Stop();
        }

        _dragOrigin = e.GetPosition(this);
        _dragArmed = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragArmed = false;
        _dragStarted = true;
        _clickCandidate = false;
        _doubleClickPending = false;
        _singleClickTimer.Stop();
        BeginDrag();
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var clickCandidate = _clickCandidate;
        _clickCandidate = false;
        _dragArmed = false;

        if (!clickCandidate || _dragStarted)
        {
            _dragStarted = false;
            _doubleClickPending = false;
            return;
        }

        if (_doubleClickPending || e.ClickCount >= 2)
        {
            _doubleClickPending = false;
            _singleClickTimer.Stop();
            ExecuteGesture(doubleClick: true);
            return;
        }

        _doubleClickPending = false;
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void BeginDrag()
    {
        var item = Item;
        if (item is null || !File.Exists(item.FilePath))
        {
            return;
        }

        var effects = DragDropEffects.None;
        try
        {
            var data = BuildDataObject(item);
            RaiseEvent(new CardDragStateEventArgs(DragStateChangedEvent, true));
            effects = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Core.Log.Error("Drag & drop failed", ex);
        }
        finally
        {
            RaiseEvent(new CardDragStateEventArgs(DragStateChangedEvent, false));
        }

        Request(CardCommand.DragCompleted, CardActionKind.None, effects);
    }

    /// <summary>
    /// Builds a rich <see cref="DataObject"/>: a file drop for Explorer/Slack desktop,
    /// a bitmap / DIB for classic Win32 consumers and PNG bytes for browser uploaders
    /// (Discord, Figma web, Slack web, …).
    /// </summary>
    private static DataObject BuildDataObject(ScreenshotItem item)
    {
        var data = new DataObject();

        data.SetFileDropList(new StringCollection { item.FilePath });

        try
        {
            var bitmap = LoadFullBitmap(item.FilePath);
            if (bitmap is not null)
            {
                data.SetImage(bitmap);
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            Core.Log.Warn($"Drag: bitmap format unavailable ({ex.Message})");
        }

        try
        {
            byte[] pngBytes = File.ReadAllBytes(item.FilePath);
            if (pngBytes.Length > 0)
            {
                data.SetData("PNG", new MemoryStream(pngBytes, writable: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn($"Drag: PNG format unavailable ({ex.Message})");
        }

        try
        {
            var dib = CreateDibStream(item.FilePath);
            if (dib is not null)
            {
                data.SetData(DataFormats.Dib, dib);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException)
        {
            Core.Log.Warn($"Drag: DIB format unavailable ({ex.Message})");
        }

        return data;
    }

    private static BitmapSource? LoadFullBitmap(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var frame = decoder.Frames[0];
        if (!frame.IsFrozen)
        {
            frame.Freeze();
        }

        return frame;
    }

    /// <summary>Extracts the raw DIB (BITMAPINFOHEADER + pixels) from a bitmap file.</summary>
    private static MemoryStream? CreateDibStream(string filePath)
    {
        using var bitmap = new Drawing.Bitmap(filePath);
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, DrawingImaging.ImageFormat.Bmp);

        var bytes = buffer.ToArray();
        const int bitmapFileHeaderSize = 14;
        if (bytes.Length <= bitmapFileHeaderSize)
        {
            return null;
        }

        return new MemoryStream(bytes, bitmapFileHeaderSize, bytes.Length - bitmapFileHeaderSize, writable: false);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void RequestAction(CardActionKind kind) =>
        Request(CardCommand.ExecuteAction, kind);

    private void Request(CardCommand command, CardActionKind kind = CardActionKind.None, DragDropEffects effect = DragDropEffects.None)
    {
        if (Item is { } item)
        {
            RaiseEvent(new CardActionEventArgs(ActionRequestedEvent, command, kind, item, effect));
        }
    }

    private static void Animate(UIElement element, DependencyProperty property, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        element.BeginAnimation(property, animation);
    }

    private static DependencyObject? GetParent(DependencyObject source) => source switch
    {
        Visual => VisualTreeHelper.GetParent(source),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(source),
    };

    private static bool IsWithinButton(DependencyObject? source)
    {
        while (source is not null && source is not ScreenshotCard)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private bool IsWithinThumbnail(DependencyObject? source)
    {
        while (source is not null && source != this)
        {
            if (ReferenceEquals(source, ThumbnailSurface))
            {
                return true;
            }

            source = GetParent(source);
        }

        return false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _singleClickTimer.Stop();

        if (_subscribedItem is not null)
        {
            _subscribedItem.PropertyChanged -= OnItemPropertyChanged;
            _subscribedItem = null;
        }

        if (e.NewValue is ScreenshotItem item)
        {
            _subscribedItem = item;
            item.PropertyChanged += OnItemPropertyChanged;
            UpdateSpinner(item.IsRecognizing);
        }
        else
        {
            UpdateSpinner(false);
        }

        RefreshSlots();
        UpdateQrPill();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ScreenshotItem item)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ScreenshotItem.IsRecognizing):
                UpdateSpinner(item.IsRecognizing);
                break;

            case nameof(ScreenshotItem.IsPinned):
                RefreshSlots();
                break;

            case nameof(ScreenshotItem.DetectedBarcode):
            case nameof(ScreenshotItem.HasDetectedBarcode):
            case nameof(ScreenshotItem.DetectedBarcodeLabel):
                UpdateQrPill();
                break;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshSlots();

    private static void OnSettingsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScreenshotCard card)
        {
            card.OnSettingsChanged(e.OldValue as OsokoSettings, e.NewValue as OsokoSettings);
        }
    }

    private void OnSettingsChanged(OsokoSettings? oldSettings, OsokoSettings? newSettings)
    {
        if (_subscribedSettings is not null)
        {
            _subscribedSettings.PropertyChanged -= OnSettingsPropertyChanged;
            _subscribedSettings = null;
        }

        if (newSettings is not null)
        {
            _subscribedSettings = newSettings;
            newSettings.PropertyChanged += OnSettingsPropertyChanged;
        }

        RefreshSlots();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedItem is null && Item is { } item)
        {
            _subscribedItem = item;
            item.PropertyChanged += OnItemPropertyChanged;
            UpdateSpinner(item.IsRecognizing);
        }

        if (_subscribedSettings is null && Settings is { } settings)
        {
            _subscribedSettings = settings;
            settings.PropertyChanged += OnSettingsPropertyChanged;
        }

        RefreshSlots();
        UpdateQrPill();
    }

    private void UpdateSpinner(bool isRecognizing)
    {
        if (isRecognizing)
        {
            if (_spinnerAnimation is not null)
            {
                return;
            }

            _spinnerAnimation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };

            SpinnerTransform.BeginAnimation(RotateTransform.AngleProperty, _spinnerAnimation);
        }
        else if (_spinnerAnimation is not null)
        {
            _spinnerAnimation = null;
            SpinnerTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            SpinnerTransform.Angle = 0;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _singleClickTimer.Stop();

        if (_subscribedItem is not null)
        {
            _subscribedItem.PropertyChanged -= OnItemPropertyChanged;
            _subscribedItem = null;
        }

        if (_subscribedSettings is not null)
        {
            _subscribedSettings.PropertyChanged -= OnSettingsPropertyChanged;
            _subscribedSettings = null;
        }
    }

    private readonly record struct ShortcutRequest(CardCommand Command, CardActionKind Kind);
}
