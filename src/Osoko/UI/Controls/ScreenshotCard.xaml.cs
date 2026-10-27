using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Osoko.Core;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace Osoko.UI.Controls;

/// <summary>Actions a <see cref="ScreenshotCard"/> can request from its host.</summary>
public enum CardAction
{
    CopyImage,
    CopyText,
    TogglePin,
    Save,
    Delete,

    /// <summary>An OLE drag finished; <see cref="CardActionEventArgs.Effect"/> carries the result.</summary>
    DragCompleted,
}

/// <summary>Payload for <see cref="ScreenshotCard.ActionRequested"/>.</summary>
public sealed class CardActionEventArgs : RoutedEventArgs
{
    public CardActionEventArgs(RoutedEvent routedEvent, CardAction action, ScreenshotItem item, DragDropEffects effect)
        : base(routedEvent)
    {
        Action = action;
        Item = item;
        Effect = effect;
    }

    public CardAction Action { get; }

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
/// One capture on the shelf: Fluent card with a high-DPI preview, a metadata strip and a
/// hover toolbar (copy image, copy OCR text, pin, save, remove). Dragging the card starts
/// a multi-format OLE drag so the screenshot can be pasted into any application.
/// </summary>
public partial class ScreenshotCard : UserControl
{
    /// <summary>Bubbling event carrying every toolbar / drag request.</summary>
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

    private Point _dragOrigin;
    private bool _dragArmed;
    private DoubleAnimation? _spinnerAnimation;
    private ScreenshotItem? _subscribedItem;

    public ScreenshotCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    private ScreenshotItem? Item => DataContext as ScreenshotItem;

    // ---------------------------------------------------------------------
    // Toolbar
    // ---------------------------------------------------------------------

    private void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardAction.CopyImage);
    }

    private void OnCopyTextClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardAction.CopyText);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardAction.TogglePin);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardAction.Save);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Request(CardAction.Delete);
    }

    // ---------------------------------------------------------------------
    // Hover visuals
    // ---------------------------------------------------------------------

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        ActionBar.IsHitTestVisible = true;
        Animate(ActionBar, OpacityProperty, 1d, 120);
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        ActionBar.IsHitTestVisible = false;
        Animate(ActionBar, OpacityProperty, 0d, 160);
    }

    // ---------------------------------------------------------------------
    // Drag & drop (CF_HDROP + CF_DIB + CF_BITMAP + PNG)
    // ---------------------------------------------------------------------

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject))
        {
            _dragArmed = false;
            return;
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
        BeginDrag();
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragArmed = false;

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

        Request(CardAction.DragCompleted, effects);
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

    private void Request(CardAction action, DragDropEffects effect = DragDropEffects.None)
    {
        if (Item is { } item)
        {
            RaiseEvent(new CardActionEventArgs(ActionRequestedEvent, action, item, effect));
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

    private static bool IsWithinButton(DependencyObject? source)
    {
        while (source is not null && source is not ScreenshotCard)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
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
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenshotItem.IsRecognizing) && sender is ScreenshotItem item)
        {
            UpdateSpinner(item.IsRecognizing);
        }
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
        if (_subscribedItem is not null)
        {
            _subscribedItem.PropertyChanged -= OnItemPropertyChanged;
            _subscribedItem = null;
        }
    }
}
