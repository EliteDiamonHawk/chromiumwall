namespace BrowserWall;

public sealed class WindowRegistry
{
    private readonly Dictionary<string, AppWindow> _byToken = new(StringComparer.Ordinal);

    public IReadOnlyCollection<AppWindow> Items => _byToken.Values;

    public void Add(AppWindow appWindow)
    {
        if (_byToken.Values.Any(existing => string.Equals(existing.LogicalId, appWindow.LogicalId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Logical window ID already registered: {appWindow.LogicalId}");
        if (!_byToken.TryAdd(appWindow.Token, appWindow))
            throw new InvalidOperationException($"Launch token already registered: {appWindow.Token}");
    }

    public bool IsHwndAssignedToAnother(IntPtr hwnd, AppWindow appWindow) =>
        hwnd != IntPtr.Zero && _byToken.Values.Any(existing =>
            !ReferenceEquals(existing, appWindow) &&
            existing.State == AppWindowState.Found &&
            existing.Hwnd == hwnd);

    public void MarkFound(AppWindow appWindow, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("A found window requires a non-zero HWND.", nameof(hwnd));
        if (IsHwndAssignedToAnother(hwnd, appWindow))
            throw new InvalidOperationException("HWND is already assigned to another app window.");

        appWindow.Hwnd = hwnd;
        appWindow.State = AppWindowState.Found;
        appWindow.FailureReason = null;
    }

    public void MarkClosed(AppWindow appWindow)
    {
        if (appWindow.State == AppWindowState.Found)
            appWindow.State = AppWindowState.Closed;
    }

    public void MarkFailed(AppWindow appWindow, string reason)
    {
        if (appWindow.State is AppWindowState.Pending or AppWindowState.Found)
        {
            appWindow.State = AppWindowState.Failed;
            appWindow.FailureReason = reason;
        }
    }
}
