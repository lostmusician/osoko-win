using System.Windows;
using System.Windows.Threading;
using Osoko.Interop;

namespace Osoko.Core;

/// <summary>Visual states of the floating shelf.</summary>
public enum ShelfState
{
    /// <summary>Full stack visible and interactive.</summary>
    Expanded,

    /// <summary>Slid off screen; only the edge grab handle remains.</summary>
    Collapsed,

    /// <summary>Collapsed stack temporarily revealed because the pointer touched the screen edge.</summary>
    HoverPeek,
}

/// <summary>Payload for <see cref="StackCollapseState.StateChanged"/>.</summary>
public sealed class ShelfStateChangedEventArgs : EventArgs
{
    public ShelfStateChangedEventArgs(ShelfState oldState, ShelfState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public ShelfState OldState { get; }

    public ShelfState NewState { get; }
}

/// <summary>
/// Owns the shelf's collapse behaviour:
/// <list type="bullet">
///   <item>the user-configurable auto-collapse timer (default 10 s of inactivity),</item>
///   <item>hover peeking when the pointer touches the right screen edge next to the shelf,</item>
///   <item>the explicit expanded / collapsed / peek transitions.</item>
/// </list>
/// The class is UI thread affine and uses <see cref="DispatcherTimer"/> so animations
/// and state changes never need marshalling.
/// </summary>
public sealed class StackCollapseState : IDisposable
{
    private static readonly TimeSpan EdgePollInterval = TimeSpan.FromMilliseconds(160);

    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _edgePoller;
    private ShelfState _state = ShelfState.Expanded;
    private bool _edgeHoverRaised;
    private bool _disposed;

    public StackCollapseState(bool autoCollapseEnabled = true, double autoCollapseDelaySeconds = 10)
    {
        AutoCollapseEnabled = autoCollapseEnabled;
        AutoCollapseDelay = TimeSpan.FromSeconds(
            Math.Clamp(autoCollapseDelaySeconds, OsokoSettings.MinCollapseDelaySeconds, OsokoSettings.MaxCollapseDelaySeconds));

        _autoCollapseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = AutoCollapseDelay,
        };
        _autoCollapseTimer.Tick += OnAutoCollapseTick;

        _edgePoller = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = EdgePollInterval,
        };
        _edgePoller.Tick += OnEdgePollTick;
    }

    /// <summary>Current shelf state.</summary>
    public ShelfState State => _state;

    public bool AutoCollapseEnabled { get; set; }

    public TimeSpan AutoCollapseDelay
    {
        get => _autoCollapseTimer.Interval;
        set => _autoCollapseTimer.Interval = value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value;
    }

    /// <summary>
    /// Interactive shelf rectangle in physical screen pixels. While the pointer is inside
    /// this area (or the edge activation band) the shelf peeks back in.
    /// </summary>
    public Rect ShelfBoundsInScreenPixels { get; set; } = Rect.Empty;

    /// <summary>
    /// Thin strip along the right screen edge that also reveals a collapsed shelf.
    /// Usually a few pixels wider than the grab handle's inset.
    /// </summary>
    public Rect EdgeActivationBandInScreenPixels { get; set; } = Rect.Empty;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<ShelfStateChangedEventArgs>? StateChanged;

    /// <summary>Raised once per hover when the pointer reaches the shelf area or the screen edge.</summary>
    public event EventHandler? EdgeHoverDetected;

    /// <summary>Raised when the pointer leaves the peek area while the shelf is only peeking.</summary>
    public event EventHandler? EdgeHoverEnded;

    public bool IsEdgePolling => _edgePoller.IsEnabled;

    /// <summary>Starts watching the screen edge so a collapsed shelf can peek back in.</summary>
    public void StartEdgePolling()
    {
        if (!_disposed && !_edgePoller.IsEnabled)
        {
            _edgePoller.Start();
        }
    }

    public void StopEdgePolling()
    {
        _edgePoller.Stop();
        _edgeHoverRaised = false;
    }

    /// <summary>Fully reveal the shelf and restart the idle timer.</summary>
    public void Expand()
    {
        TransitionTo(ShelfState.Expanded);
        RestartAutoCollapseTimer();
    }

    /// <summary>Reveal the shelf because the pointer touched the screen edge; do not arm the idle timer yet.</summary>
    public void EnterHoverPeek()
    {
        if (_state == ShelfState.Expanded)
        {
            return;
        }

        TransitionTo(ShelfState.HoverPeek);
    }

    /// <summary>Slide the shelf away, leaving only the grab handle.</summary>
    public void Collapse()
    {
        _autoCollapseTimer.Stop();
        TransitionTo(ShelfState.Collapsed);
    }

    public void Toggle()
    {
        if (_state == ShelfState.Expanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    /// <summary>
    /// Called on every meaningful pointer interaction with the shelf. Restarts the
    /// inactivity countdown so the shelf does not disappear while it is being used.
    /// </summary>
    public void NotifyPointerActivity()
    {
        if (_state == ShelfState.Expanded)
        {
            RestartAutoCollapseTimer();
        }
    }

    /// <summary>The pointer left the shelf. Peek collapses instantly, expansion arms the timer.</summary>
    public void NotifyPointerLeftShelf()
    {
        switch (_state)
        {
            case ShelfState.HoverPeek:
                Collapse();
                break;
            case ShelfState.Expanded when AutoCollapseEnabled:
                RestartAutoCollapseTimer();
                break;
        }
    }

    /// <summary>Re-arms the timer after settings changed.</summary>
    public void ApplyTiming(bool autoCollapseEnabled, int delaySeconds)
    {
        AutoCollapseEnabled = autoCollapseEnabled;
        AutoCollapseDelay = TimeSpan.FromSeconds(Math.Clamp(
            delaySeconds,
            OsokoSettings.MinCollapseDelaySeconds,
            OsokoSettings.MaxCollapseDelaySeconds));

        if (autoCollapseEnabled && _state == ShelfState.Expanded)
        {
            RestartAutoCollapseTimer();
        }
        else
        {
            _autoCollapseTimer.Stop();
        }
    }

    /// <summary>Forces a state without animating. Used when the shelf is shown/hidden.</summary>
    internal void Reset(ShelfState state)
    {
        _autoCollapseTimer.Stop();
        TransitionTo(state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoCollapseTimer.Stop();
        _edgePoller.Stop();
        _autoCollapseTimer.Tick -= OnAutoCollapseTick;
        _edgePoller.Tick -= OnEdgePollTick;
    }

    private void RestartAutoCollapseTimer()
    {
        _autoCollapseTimer.Stop();
        if (AutoCollapseEnabled)
        {
            _autoCollapseTimer.Interval = AutoCollapseDelay;
            _autoCollapseTimer.Start();
        }
    }

    private void OnAutoCollapseTick(object? sender, EventArgs e)
    {
        _autoCollapseTimer.Stop();
        if (AutoCollapseEnabled && _state == ShelfState.Expanded)
        {
            Collapse();
        }
    }

    private void OnEdgePollTick(object? sender, EventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var point = new Point(cursor.X, cursor.Y);
        var insideShelf = !ShelfBoundsInScreenPixels.IsEmpty && ShelfBoundsInScreenPixels.Contains(point);
        var insideEdgeBand = !EdgeActivationBandInScreenPixels.IsEmpty && EdgeActivationBandInScreenPixels.Contains(point);

        if (insideShelf || insideEdgeBand)
        {
            if (!_edgeHoverRaised && _state != ShelfState.Expanded)
            {
                _edgeHoverRaised = true;
                EdgeHoverDetected?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (!_edgeHoverRaised)
        {
            return;
        }

        _edgeHoverRaised = false;

        // A pure peek must not linger after the pointer has moved away; the window
        // decides whether to animate back to the collapsed state.
        if (_state == ShelfState.HoverPeek)
        {
            EdgeHoverEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TransitionTo(ShelfState newState)
    {
        if (_state == newState)
        {
            return;
        }

        var oldState = _state;
        _state = newState;

        if (newState != ShelfState.Collapsed)
        {
            _edgeHoverRaised = false;
        }

        StateChanged?.Invoke(this, new ShelfStateChangedEventArgs(oldState, newState));
    }
}
