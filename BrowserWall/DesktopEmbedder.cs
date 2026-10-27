using System.Runtime.InteropServices;

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

        LogWindowState("before", appWindow.Hwnd, host);

        long style = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, "read style").ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_MINIMIZE | NativeMethods.WS_MAXIMIZEBOX |
                   NativeMethods.WS_SYSMENU | NativeMethods.WS_POPUP);
        style |= NativeMethods.WS_CHILD;

        long exStyle = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, "read extended style").ToInt64();
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        SetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, new IntPtr(style), "set style");
        SetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle), "set extended style");

        Marshal.SetLastPInvokeError(0);
        IntPtr previousParent = NativeMethods.SetParent(appWindow.Hwnd, host);
        int parentError = Marshal.GetLastPInvokeError();
        if (previousParent == IntPtr.Zero && parentError != 0)
            throw new InvalidOperationException($"SetParent failed: {FormatWin32Error(parentError)}");

        IntPtr actualParent = NativeMethods.GetParent(appWindow.Hwnd);
        Console.WriteLine($"[EmbedCheck] parent requested={FormatHwnd(host)}, previous={FormatHwnd(previousParent)}, actual={FormatHwnd(actualParent)}");
        if (actualParent != host)
            throw new InvalidOperationException($"SetParent verification failed: expected {FormatHwnd(host)}, got {FormatHwnd(actualParent)}.");

        Rectangle bounds = appWindow.Spec.Bounds;
        Rectangle virtualBounds = appWindow.Spec.VirtualBounds;
        int x = bounds.Left - virtualBounds.Left;
        int y = bounds.Top - virtualBounds.Top;
        Rectangle hostClient = GetClientRectChecked(host);
        NativeMethods.POINT mappedTopLeft = new() { X = bounds.Left, Y = bounds.Top };
        NativeMethods.POINT mappedBottomRight = new() { X = bounds.Right, Y = bounds.Bottom };
        bool mappedTopLeftOk = NativeMethods.ScreenToClient(host, ref mappedTopLeft);
        int mapTopLeftError = mappedTopLeftOk ? 0 : Marshal.GetLastPInvokeError();
        bool mappedBottomRightOk = NativeMethods.ScreenToClient(host, ref mappedBottomRight);
        int mapBottomRightError = mappedBottomRightOk ? 0 : Marshal.GetLastPInvokeError();
        Console.WriteLine(
            $"[EmbedCheck] hostRect={FormatWindowRect(host)}, hostClient={hostClient}, " +
            $"requestedScreen={bounds}, virtualBounds={virtualBounds}, " +
            $"assumedChild=({x},{y},{bounds.Width}x{bounds.Height}), " +
            $"mappedChild={(mappedTopLeftOk && mappedBottomRightOk ? $"({mappedTopLeft.X},{mappedTopLeft.Y},{mappedBottomRight.X - mappedTopLeft.X}x{mappedBottomRight.Y - mappedTopLeft.Y})" : "FAILED")}");
        if (!mappedTopLeftOk || !mappedBottomRightOk)
            Console.WriteLine($"[EmbedCheck] ScreenToClient failed: topLeft={FormatWin32Error(mapTopLeftError)}, bottomRight={FormatWin32Error(mapBottomRightError)}");
        else if (x != mappedTopLeft.X || y != mappedTopLeft.Y)
            Console.WriteLine($"[EmbedCheck] WARNING: assumed child origin differs from host-relative origin by ({x - mappedTopLeft.X},{y - mappedTopLeft.Y}).");

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
            throw new InvalidOperationException(
                $"Could not position browser window: {appWindow.LogicalId}; {FormatWin32Error(Marshal.GetLastPInvokeError())}");
        }

        LogWindowState("after", appWindow.Hwnd, host);
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

    private static IntPtr GetWindowLongPtrChecked(IntPtr hwnd, int index, string operation)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr value = NativeMethods.GetWindowLongPtrW(hwnd, index);
        int error = Marshal.GetLastPInvokeError();
        if (value == IntPtr.Zero && error != 0)
            throw new InvalidOperationException($"Could not {operation} for {FormatHwnd(hwnd)}: {FormatWin32Error(error)}");
        return value;
    }

    private static void SetWindowLongPtrChecked(IntPtr hwnd, int index, IntPtr value, string operation)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = NativeMethods.SetWindowLongPtrW(hwnd, index, value);
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
            throw new InvalidOperationException($"Could not {operation} for {FormatHwnd(hwnd)}: {FormatWin32Error(error)}");

        IntPtr actual = GetWindowLongPtrChecked(hwnd, index, $"verify {operation}");
        Console.WriteLine($"[EmbedCheck] {operation}: hwnd={FormatHwnd(hwnd)}, previous=0x{previous.ToInt64():X}, requested=0x{value.ToInt64():X}, actual=0x{actual.ToInt64():X}");
        if (actual != value)
            throw new InvalidOperationException($"{operation} verification failed for {FormatHwnd(hwnd)}: requested 0x{value.ToInt64():X}, got 0x{actual.ToInt64():X}.");
    }

    private static Rectangle GetClientRectChecked(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            throw new InvalidOperationException($"Could not read client rect for {FormatHwnd(hwnd)}: {FormatWin32Error(Marshal.GetLastPInvokeError())}");
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static string FormatWindowRect(IntPtr hwnd) =>
        NativeMethods.TryGetWindowRect(hwnd, out Rectangle rect) ? rect.ToString() : $"FAILED ({FormatWin32Error(Marshal.GetLastPInvokeError())})";

    private static void LogWindowState(string phase, IntPtr hwnd, IntPtr host)
    {
        IntPtr style = GetWindowLongPtrChecked(hwnd, NativeMethods.GWL_STYLE, $"read style ({phase})");
        IntPtr exStyle = GetWindowLongPtrChecked(hwnd, NativeMethods.GWL_EXSTYLE, $"read extended style ({phase})");
        Console.WriteLine(
            $"[EmbedCheck] {phase}: hwnd={FormatHwnd(hwnd)}, parent={FormatHwnd(NativeMethods.GetParent(hwnd))}, " +
            $"host={FormatHwnd(host)}, style=0x{style.ToInt64():X}, exStyle=0x{exStyle.ToInt64():X}, " +
            $"rect={FormatWindowRect(hwnd)}, hostRect={FormatWindowRect(host)}");
    }

    private static string FormatWin32Error(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}";
}
