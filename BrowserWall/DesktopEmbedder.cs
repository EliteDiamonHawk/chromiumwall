using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BrowserWall;

public sealed class DesktopEmbedder
{
    private const string ShellViewClass = "SHELLDLL_DefView";
    private const string WorkerWClass = "WorkerW";
    private const long FrameStyleMask = NativeMethods.WS_CAPTION |
                                         NativeMethods.WS_THICKFRAME |
                                         NativeMethods.WS_MINIMIZEBOX |
                                         NativeMethods.WS_MAXIMIZEBOX |
                                         NativeMethods.WS_SYSMENU;

    private readonly bool _verbose;

    public DesktopEmbedder(bool verbose = false) => _verbose = verbose;

    public static long GetBorderlessChildStyle(long style) =>
        (style & ~(FrameStyleMask | NativeMethods.WS_POPUP)) | NativeMethods.WS_CHILD;

    public static long GetEmbeddedExtendedStyle(long exStyle) =>
        (exStyle & ~NativeMethods.WS_EX_APPWINDOW) |
        NativeMethods.WS_EX_TOOLWINDOW |
        NativeMethods.WS_EX_NOACTIVATE;

    public static bool HasNativeFrame(long style) => (style & FrameStyleMask) != 0;

    public static bool TryCalculateNonClientInsets(
        Rectangle windowScreenBounds,
        Rectangle clientScreenBounds,
        out NonClientInsets insets)
    {
        insets = default;
        if (windowScreenBounds.Width <= 0 || windowScreenBounds.Height <= 0 ||
            clientScreenBounds.Width <= 0 || clientScreenBounds.Height <= 0 ||
            clientScreenBounds.Left < windowScreenBounds.Left ||
            clientScreenBounds.Top < windowScreenBounds.Top ||
            clientScreenBounds.Right > windowScreenBounds.Right ||
            clientScreenBounds.Bottom > windowScreenBounds.Bottom)
        {
            return false;
        }

        insets = new NonClientInsets(
            clientScreenBounds.Left - windowScreenBounds.Left,
            clientScreenBounds.Top - windowScreenBounds.Top,
            windowScreenBounds.Right - clientScreenBounds.Right,
            windowScreenBounds.Bottom - clientScreenBounds.Bottom);
        return true;
    }

    public static Rectangle CalculateOuterBounds(Rectangle targetClientBounds, NonClientInsets insets) =>
        new(
            targetClientBounds.Left - insets.Left,
            targetClientBounds.Top - insets.Top,
            checked(targetClientBounds.Width + insets.Left + insets.Right),
            checked(targetClientBounds.Height + insets.Top + insets.Bottom));

    public static Rectangle ToParentClient(Rectangle screenBounds, Point parentClientOrigin) =>
        new(screenBounds.Left - parentClientOrigin.X, screenBounds.Top - parentClientOrigin.Y,
            screenBounds.Width, screenBounds.Height);

    public void Embed(AppWindow appWindow) => Reconcile(appWindow);

    public void Reconcile(AppWindow appWindow)
    {
        if (!NativeMethods.IsWindow(appWindow.Hwnd))
            throw new InvalidOperationException($"Window is no longer valid: {appWindow.LogicalId}");

        IntPtr host = FindDesktopHost();
        if (host == IntPtr.Zero)
            throw new InvalidOperationException("Could not find the Windows desktop host window.");

        CaptureOriginalState(appWindow);

        long currentStyle = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, "read style").ToInt64();
        long currentExStyle = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, "read extended style").ToInt64();
        long desiredStyle = GetBorderlessChildStyle(currentStyle);
        long desiredExStyle = GetEmbeddedExtendedStyle(currentExStyle);
        bool styleChanged = currentStyle != desiredStyle;
        bool exStyleChanged = currentExStyle != desiredExStyle;

        if (styleChanged)
            SetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, new IntPtr(desiredStyle), "set style");
        if (exStyleChanged)
            SetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desiredExStyle), "set extended style");

        if (styleChanged || exStyleChanged)
        {
            if (!NativeMethods.SetWindowPos(appWindow.Hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_FRAMECHANGED))
            {
                throw new InvalidOperationException($"Could not recalculate window frame: {FormatWin32Error(Marshal.GetLastPInvokeError())}");
            }
        }

        bool parentChanged = NativeMethods.GetParent(appWindow.Hwnd) != host;
        if (parentChanged)
        {
            Marshal.SetLastPInvokeError(0);
            IntPtr previousParent = NativeMethods.SetParent(appWindow.Hwnd, host);
            int parentError = Marshal.GetLastPInvokeError();
            if (previousParent == IntPtr.Zero && parentError != 0)
                throw new InvalidOperationException($"SetParent failed: {FormatWin32Error(parentError)}");
        }

        if (NativeMethods.GetParent(appWindow.Hwnd) != host)
            throw new InvalidOperationException($"SetParent verification failed for {FormatHwnd(appWindow.Hwnd)}.");

        MonitorGeometry geometry = ResolveGeometry(appWindow);
        NativeMethods.POINT[] points =
        {
            new() { X = geometry.Bounds.Left, Y = geometry.Bounds.Top },
            new() { X = geometry.Bounds.Right, Y = geometry.Bounds.Bottom }
        };

        Marshal.SetLastPInvokeError(0);
        int mapResult = NativeMethods.MapWindowPoints(IntPtr.Zero, host, points, 2);
        int mapError = Marshal.GetLastPInvokeError();
        if (mapResult == 0 && mapError != 0)
            throw new InvalidOperationException($"Could not map monitor bounds into WorkerW coordinates: {FormatWin32Error(mapError)}");

        Rectangle childBounds = new(points[0].X, points[0].Y,
            points[1].X - points[0].X, points[1].Y - points[0].Y);
        if (!TryMeasureWindowGeometry(appWindow.Hwnd, out WindowGeometry actualBeforeGeometry))
            throw new InvalidOperationException(
                $"Could not measure browser client geometry before placement for {appWindow.LogicalId}.");

        NonClientInsets insets = actualBeforeGeometry.Insets;
        Rectangle requestedOuterBounds = CalculateOuterBounds(childBounds, insets);
        bool needsPosition = parentChanged || actualBeforeGeometry.ClientScreenBounds != geometry.Bounds;

        LogGeometry(appWindow, host, geometry, childBounds, insets, requestedOuterBounds,
            actualBeforeGeometry, null);

        if (needsPosition && !NativeMethods.SetWindowPos(appWindow.Hwnd, NativeMethods.HWND_BOTTOM,
                requestedOuterBounds.X, requestedOuterBounds.Y,
                requestedOuterBounds.Width, requestedOuterBounds.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_NOSENDCHANGING))
        {
            throw new InvalidOperationException(
                $"Could not position browser window: {appWindow.LogicalId}; {FormatWin32Error(Marshal.GetLastPInvokeError())}");
        }

        if (NativeMethods.GetParent(appWindow.Hwnd) != host)
            throw new InvalidOperationException($"Embedded parent changed unexpectedly for {appWindow.LogicalId}.");
        if (!TryMeasureWindowGeometry(appWindow.Hwnd, out WindowGeometry actualAfterGeometry))
            throw new InvalidOperationException($"Could not verify browser geometry for {appWindow.LogicalId}.");
        if (actualAfterGeometry.ClientScreenBounds != geometry.Bounds)
            throw new InvalidOperationException(
                $"Browser client geometry mismatch for {appWindow.LogicalId}: " +
                $"requested monitor {geometry.Bounds}, actual client {actualAfterGeometry.ClientScreenBounds}, " +
                $"outer {actualAfterGeometry.WindowScreenBounds}.");

        LogGeometry(appWindow, host, geometry, childBounds, insets, requestedOuterBounds,
            actualBeforeGeometry, actualAfterGeometry);
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

        NativeMethods.SendMessageTimeoutW(progman, NativeMethods.WM_SPAWN_WORKERW,
            IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
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

    private MonitorGeometry ResolveGeometry(AppWindow appWindow)
    {
        if (appWindow.LogicalId == "virtual-screen")
            return new MonitorGeometry(IntPtr.Zero, appWindow.Spec.VirtualBounds, Rectangle.Empty);

        Rectangle requested = appWindow.Spec.Bounds;
        NativeMethods.POINT center = new()
        {
            X = requested.Left + requested.Width / 2,
            Y = requested.Top + requested.Height / 2
        };
        IntPtr monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException($"Could not resolve monitor for {appWindow.LogicalId}.");

        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
            throw new InvalidOperationException($"Could not read monitor geometry: {FormatWin32Error(Marshal.GetLastPInvokeError())}");

        return new MonitorGeometry(monitor,
            Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
    }

    private static void CaptureOriginalState(AppWindow appWindow)
    {
        if (appWindow.OriginalHwnd == appWindow.Hwnd && appWindow.OriginalNativeState != null)
            return;

        long style = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, "read original style").ToInt64();
        long exStyle = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, "read original extended style").ToInt64();
        if (!NativeMethods.TryGetWindowRect(appWindow.Hwnd, out Rectangle bounds))
            throw new InvalidOperationException($"Could not read original window bounds: {FormatWin32Error(Marshal.GetLastPInvokeError())}");

        appWindow.OriginalHwnd = appWindow.Hwnd;
        appWindow.OriginalNativeState = new NativeWindowSnapshot(style, exStyle,
            NativeMethods.GetParent(appWindow.Hwnd), bounds);
    }

    private void LogGeometry(AppWindow appWindow, IntPtr host, MonitorGeometry geometry,
        Rectangle targetClientBounds, NonClientInsets insets, Rectangle requestedOuterBounds,
        WindowGeometry actualBefore, WindowGeometry? actualAfter)
    {
        if (!_verbose)
            return;

        Rectangle hostRect = NativeMethods.TryGetWindowRect(host, out Rectangle readHostRect)
            ? readHostRect : Rectangle.Empty;
        Rectangle hostClient = GetClientRect(host);
        long style = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_STYLE, "read diagnostic style").ToInt64();
        long exStyle = GetWindowLongPtrChecked(appWindow.Hwnd, NativeMethods.GWL_EXSTYLE, "read diagnostic extended style").ToInt64();
        Console.WriteLine(
            $"[EmbedCheck] logicalId={appWindow.LogicalId}, token={appWindow.Token}, hwnd={FormatHwnd(appWindow.Hwnd)}, " +
            $"monitor={FormatHwnd(geometry.Monitor)}, rcMonitor={geometry.Bounds}, rcWork={geometry.Work}, " +
            $"workerRect={hostRect}, workerClient={hostClient}, targetClientParent={targetClientBounds}, " +
            $"windowBefore={actualBefore.WindowScreenBounds}, clientLocalBefore={actualBefore.ClientLocalBounds}, " +
            $"clientScreenBefore={actualBefore.ClientScreenBounds}, nonClient={insets}, " +
            $"requestedOuter={requestedOuterBounds}, " +
            $"windowAfter={actualAfter?.WindowScreenBounds}, clientLocalAfter={actualAfter?.ClientLocalBounds}, " +
            $"clientScreenAfter={actualAfter?.ClientScreenBounds}, " +
            $"style=0x{style:X}, exStyle=0x{exStyle:X}, browserDpi={NativeMethods.GetDpiForWindow(appWindow.Hwnd)}, " +
            $"workerDpi={NativeMethods.GetDpiForWindow(host)}");
    }

    private static bool TryMeasureWindowGeometry(IntPtr hwnd, out WindowGeometry geometry)
    {
        geometry = default!;
        if (!NativeMethods.TryGetWindowRect(hwnd, out Rectangle windowScreenBounds) ||
            !NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT clientRect))
        {
            return false;
        }

        NativeMethods.POINT[] clientCorners =
        {
            new() { X = clientRect.Left, Y = clientRect.Top },
            new() { X = clientRect.Right, Y = clientRect.Bottom }
        };
        Marshal.SetLastPInvokeError(0);
        int mapResult = NativeMethods.MapWindowPoints(hwnd, IntPtr.Zero, clientCorners, 2);
        int mapError = Marshal.GetLastPInvokeError();
        if (mapResult == 0 && mapError != 0)
            return false;

        Rectangle clientLocalBounds = Rectangle.FromLTRB(
            clientRect.Left, clientRect.Top, clientRect.Right, clientRect.Bottom);
        Rectangle clientScreenBounds = Rectangle.FromLTRB(
            clientCorners[0].X, clientCorners[0].Y,
            clientCorners[1].X, clientCorners[1].Y);
        if (!TryCalculateNonClientInsets(windowScreenBounds, clientScreenBounds, out NonClientInsets insets))
            return false;

        geometry = new WindowGeometry(windowScreenBounds, clientLocalBounds, clientScreenBounds, insets);
        return true;
    }

    private static Rectangle GetClientRect(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            return Rectangle.Empty;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static string FormatHwnd(IntPtr hwnd) => hwnd == IntPtr.Zero ? "0x0" : $"0x{hwnd.ToInt64():X}";

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
        if (actual != value)
            throw new InvalidOperationException(
                $"{operation} verification failed for {FormatHwnd(hwnd)}: requested 0x{value.ToInt64():X}, got 0x{actual.ToInt64():X}.");
    }

    private static string FormatWin32Error(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new Win32Exception(error).Message}";

    private sealed record WindowGeometry(
        Rectangle WindowScreenBounds,
        Rectangle ClientLocalBounds,
        Rectangle ClientScreenBounds,
        NonClientInsets Insets);

    private sealed record MonitorGeometry(IntPtr Monitor, Rectangle Bounds, Rectangle Work);
}

public readonly record struct NonClientInsets(int Left, int Top, int Right, int Bottom);
