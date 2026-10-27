namespace BrowserWall;

public sealed class DesktopEmbedder
{
    private const string ShellViewClass = "SHELLDLL_DefView";
    private const string WorkerWClass = "WorkerW";

    public void Embed(AppWindow appWindow)
    {
        if (!NativeMethods.IsWindow(appWindow.Hwnd))
            throw new InvalidOperationException($"Window is no longer valid: {appWindow.LogicalId}");

        IntPtr host = FindDesktopHost();
        if (host == IntPtr.Zero)
            throw new InvalidOperationException("Could not find the Windows desktop host window.");

        long style = NativeMethods.GetWindowLongPtrW(appWindow.Hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_MINIMIZE | NativeMethods.WS_MAXIMIZEBOX |
                   NativeMethods.WS_SYSMENU | NativeMethods.WS_POPUP);
        style |= NativeMethods.WS_CHILD;

        long exStyle = NativeMethods.GetWindowLongPtrW(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        NativeMethods.SetWindowLongPtrW(appWindow.Hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
        NativeMethods.SetWindowLongPtrW(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        NativeMethods.SetParent(appWindow.Hwnd, host);

        Rectangle bounds = appWindow.Spec.Bounds;
        Rectangle virtualBounds = appWindow.Spec.VirtualBounds;
        int x = bounds.Left - virtualBounds.Left;
        int y = bounds.Top - virtualBounds.Top;
        if (!NativeMethods.SetWindowPos(
                appWindow.Hwnd,
                NativeMethods.HWND_BOTTOM,
                x,
                y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOSENDCHANGING))
        {
            throw new InvalidOperationException($"Could not position browser window: {appWindow.LogicalId}");
        }

        Console.WriteLine($"[Embed] {appWindow.LogicalId} -> {FormatHwnd(appWindow.Hwnd)} bounds={bounds}");
    }

    public void Close(AppWindow appWindow)
    {
        if (appWindow.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(appWindow.Hwnd))
            NativeMethods.PostMessageW(appWindow.Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static IntPtr FindDesktopHost()
    {
        IntPtr progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        NativeMethods.SendMessageTimeoutW(progman, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        IntPtr shellViewInProgman = NativeMethods.FindWindowExW(progman, IntPtr.Zero, ShellViewClass, null);
        if (shellViewInProgman != IntPtr.Zero)
        {
            IntPtr childWorker = NativeMethods.FindWindowExW(progman, IntPtr.Zero, WorkerWClass, null);
            return childWorker != IntPtr.Zero ? childWorker : progman;
        }

        IntPtr worker = IntPtr.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            IntPtr shellView = NativeMethods.FindWindowExW(top, IntPtr.Zero, ShellViewClass, null);
            if (shellView == IntPtr.Zero)
                return true;

            worker = NativeMethods.FindWindowExW(IntPtr.Zero, top, WorkerWClass, null);
            return false;
        }, IntPtr.Zero);

        return worker != IntPtr.Zero ? worker : progman;
    }

    private static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";
}
