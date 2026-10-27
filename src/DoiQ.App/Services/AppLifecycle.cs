namespace DoiQ.App.Services;

/// <summary>Whether the window is in front, and when it comes back (notifications are only for when it is not).</summary>
public sealed class AppLifecycle
{
    public bool IsForeground { get; private set; } = true;

    public event Action? Resumed;

    public void Attach(Window window)
    {
        window.Activated += (_, _) => Set(true);
        window.Resumed += (_, _) => Set(true);
        window.Deactivated += (_, _) => IsForeground = false;
        window.Stopped += (_, _) => IsForeground = false;
    }

    private void Set(bool foreground)
    {
        var back = foreground && !IsForeground;
        IsForeground = foreground;
        if (back) Resumed?.Invoke();
    }
}
