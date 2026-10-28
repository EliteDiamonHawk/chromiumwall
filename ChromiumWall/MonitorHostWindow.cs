using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChromiumWall;

internal static class MonitorHostWindow
{
    private const string ClassName = "ChromiumWallMonitorHost";
    private const int ErrorClassAlreadyExists = 1410;
    private static readonly object SyncRoot = new();
    private static readonly NativeMethods.WndProc WindowProcedure = WndProc;
    private static bool _classRegistered;

    public static long Style => NativeMethods.WS_CHILD |
                                NativeMethods.WS_VISIBLE |
                                NativeMethods.WS_CLIPCHILDREN |
                                NativeMethods.WS_CLIPSIBLINGS;

    public static IntPtr Ensure(IntPtr existing, IntPtr worker, Rectangle bounds)
    {
        if (worker == IntPtr.Zero)
            throw new ArgumentException("WorkerW is required.", nameof(worker));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        if (NativeMethods.IsWindow(existing) && NativeMethods.GetParent(existing) == worker)
        {
            Position(existing, bounds);
            return existing;
        }

        RegisterClass();
        IntPtr host = NativeMethods.CreateWindowExW(
            0,
            ClassName,
            null,
            unchecked((uint)Style),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            worker,
            IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null),
            IntPtr.Zero);

        if (host == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Could not create monitor host: {FormatWin32Error(Marshal.GetLastPInvokeError())}");

        Position(host, bounds);
        return host;
    }

    public static void Destroy(IntPtr host)
    {
        if (host == IntPtr.Zero || !NativeMethods.IsWindow(host))
            return;

        if (!NativeMethods.DestroyWindow(host))
            Console.Error.WriteLine(
                $"[Embed] Could not destroy monitor host {FormatHwnd(host)}: " +
                FormatWin32Error(Marshal.GetLastPInvokeError()));
    }

    private static void RegisterClass()
    {
        lock (SyncRoot)
        {
            if (_classRegistered)
                return;

            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                WindowProcedure = WindowProcedure,
                Instance = NativeMethods.GetModuleHandleW(null),
                ClassName = ClassName
            };

            Marshal.SetLastPInvokeError(0);
            if (NativeMethods.RegisterClassExW(ref windowClass) == 0 &&
                Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Could not register monitor host class: " +
                    FormatWin32Error(Marshal.GetLastPInvokeError()));
            }

            _classRegistered = true;
        }
    }

    private static void Position(IntPtr host, Rectangle bounds)
    {
        if (!NativeMethods.SetWindowPos(
                host,
                NativeMethods.HWND_BOTTOM,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_NOSENDCHANGING |
                NativeMethods.SWP_NOOWNERZORDER))
        {
            throw new InvalidOperationException(
                $"Could not position monitor host {FormatHwnd(host)}: " +
                FormatWin32Error(Marshal.GetLastPInvokeError()));
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_ERASEBKGND)
            return new IntPtr(1);

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    private static string FormatWin32Error(int error) =>
        error == 0 ? "no Win32 error" : $"Win32 error {error}: {new Win32Exception(error).Message}";
}
