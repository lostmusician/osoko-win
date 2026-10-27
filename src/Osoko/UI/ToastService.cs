namespace Osoko.UI;

/// <summary>
/// Process-wide toast channel. Cards raise toasts ("Text copied ✓") without knowing
/// which window is currently visible; the floating shelf listens and animates a banner.
/// </summary>
public static class ToastService
{
    /// <summary>Raised on the calling thread (already the UI thread for card actions).</summary>
    public static event Action<string>? Requested;

    public static void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var handlers = Requested;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                Core.Log.Error("Toast handler failed", ex);
            }
        }
    }
}
