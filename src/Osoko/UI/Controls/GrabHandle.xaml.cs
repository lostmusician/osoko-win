using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Osoko.UI.Controls;

/// <summary>
/// The slim pill that hugs the right screen edge while the shelf is collapsed.
/// It shows how many captures are waiting and reveals the shelf on hover or click.
/// </summary>
public partial class GrabHandle : UserControl
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count),
        typeof(int),
        typeof(GrabHandle),
        new PropertyMetadata(0, OnCountChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(GrabHandle),
        new PropertyMetadata(true, OnIsExpandedChanged));

    public GrabHandle()
    {
        InitializeComponent();
        UpdateCount(Count);
        UpdateChevron(IsExpanded);
    }

    /// <summary>Number of captures currently on the shelf.</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Whether the shelf is currently expanded (flips the chevron direction).</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Raised when the user clicks the handle.</summary>
    public event EventHandler? ToggleRequested;

    private static void OnCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GrabHandle handle && e.NewValue is int count)
        {
            handle.UpdateCount(count);
        }
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GrabHandle handle && e.NewValue is bool expanded)
        {
            handle.UpdateChevron(expanded);
        }
    }

    private void UpdateCount(int count)
    {
        CountText.Text = count > 99 ? "99+" : count.ToString();
        CountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = count == 1 ? "Osoko — 1 capture" : $"Osoko — {count} captures";
    }

    private void UpdateChevron(bool expanded)
    {
        // E76B = ChevronLeft (expand), E76C = ChevronRight (collapse).
        Chevron.Text = expanded ? "\uE76C" : "\uE76B";
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        AnimateOpacity(1d, 90);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateOpacity(0.82d, 180);
    }

    private void AnimateOpacity(double target, int milliseconds)
    {
        Pill.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
