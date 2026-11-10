using System.Runtime.InteropServices;
using System.Text;

namespace ChromiumWall;

public sealed class WallpaperWindow
{
    public WallpaperWindow(AppWindow appWindow) => AppWindow = appWindow;
    public AppWindow AppWindow { get; }
    public Rectangle Bounds => AppWindow.Spec.Bounds;
}

public enum KeyboardInputState
{
    NoBackgroundKeyboardInput,
    PendingBackgroundKeyboardInteraction,
    BackgroundKeyboardInteraction
}

public enum KeyboardFocusKind
{
    None,
    RendererFallback
}

public static class KeyboardInputPolicy
{
    public static bool IsSystemOwnedKey(
        int message,
        int key,
        bool altHeld,
        bool winHeld,
        bool ctrlHeld)
    {
        if (message is NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP)
            return true;

        if (key is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or
            NativeMethods.VK_LMENU or NativeMethods.VK_RMENU or
            NativeMethods.VK_ESCAPE or NativeMethods.VK_SNAPSHOT or
            NativeMethods.VK_APPS or NativeMethods.VK_PAUSE)
        {
            return true;
        }

        if (key is >= 0x70 and <= 0x87)
            return true;

        if (key is NativeMethods.VK_CAPITAL or NativeMethods.VK_NUMLOCK or NativeMethods.VK_SCROLL)
            return true;

        return altHeld || winHeld ||
            (key == NativeMethods.VK_TAB && altHeld) ||
            (key == NativeMethods.VK_ESCAPE && ctrlHeld);
    }

    public static bool EndsOwnership(int message, int key, bool altHeld, bool winHeld, bool ctrlHeld)
    {
        if (key is NativeMethods.VK_CAPITAL or NativeMethods.VK_NUMLOCK or NativeMethods.VK_SCROLL)
            return false;

        return IsSystemOwnedKey(message, key, altHeld, winHeld, ctrlHeld);
    }
}

public sealed class KeyboardOwnershipState
{
    public KeyboardInputState State { get; private set; } = KeyboardInputState.NoBackgroundKeyboardInput;
    public KeyboardFocusKind FocusKind { get; private set; } = KeyboardFocusKind.None;
    public IntPtr Target { get; private set; }
    public IntPtr Foreground { get; private set; }

    public void Arm(IntPtr target)
    {
        if (target == IntPtr.Zero)
        {
            End();
            return;
        }

        State = KeyboardInputState.PendingBackgroundKeyboardInteraction;
        Target = target;
        Foreground = IntPtr.Zero;
        FocusKind = KeyboardFocusKind.None;
    }

    public bool TryBegin(IntPtr foreground, KeyboardFocusKind focusKind)
    {
        if (State != KeyboardInputState.PendingBackgroundKeyboardInteraction ||
            Target == IntPtr.Zero || foreground == IntPtr.Zero ||
            focusKind == KeyboardFocusKind.None)
        {
            return false;
        }

        State = KeyboardInputState.BackgroundKeyboardInteraction;
        Foreground = foreground;
        FocusKind = focusKind;
        return true;
    }

    public bool IsValid(IntPtr target, IntPtr foreground) =>
        State == KeyboardInputState.BackgroundKeyboardInteraction &&
        Target == target &&
        Foreground == foreground;

    public void End()
    {
        State = KeyboardInputState.NoBackgroundKeyboardInput;
        Target = IntPtr.Zero;
        Foreground = IntPtr.Zero;
        FocusKind = KeyboardFocusKind.None;
    }
}

public sealed class InputRouter : IDisposable
{
    private readonly Func<IReadOnlyList<WallpaperWindow>> _windows;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly KeyboardOwnershipState _keyboardOwnership = new();
    private readonly Dictionary<int, NativeMethods.KBDLLHOOKSTRUCT> _forwardedKeys = new();
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private WallpaperWindow? _keyboardWindow;
    private bool _interactingWithDesktop;

    public InputRouter(Func<IReadOnlyList<WallpaperWindow>> windows)
    {
        _windows = windows;
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        IntPtr module = NativeMethods.GetModuleHandleW(null);
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            Console.Error.WriteLine("[Input] Could not install one or more global input hooks.");
    }

    public void Dispose()
    {
        EndKeyboardInteraction("router disposed");

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    public void CancelKeyboardInteraction(string reason) => EndKeyboardInteraction(reason);

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);

        int message = (int)wParam;
        NativeMethods.MSLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
        NativeMethods.POINT point = info.pt;

        if (_interactingWithDesktop)
        {
            if (message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP)
                _interactingWithDesktop = false;
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        IntPtr underPointer = NativeMethods.WindowFromPoint(point);
        bool desktopSurface = IsDesktopSurface(underPointer);
        WallpaperWindow? target = FindWallpaperWindow(point);

        if (desktopSurface)
        {
            if (message is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP)
            {
                if (message == NativeMethods.WM_RBUTTONDOWN)
                    EndKeyboardInteraction("desktop context menu interaction");
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONDBLCLK)
            {
                if (IsDesktopIcon(underPointer, point))
                {
                    EndKeyboardInteraction("desktop icon interaction");
                    _interactingWithDesktop = true;
                    return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
                }

                NativeMethods.TryClearDesktopSelection(underPointer);
                CloseDesktopMenus();
            }

            if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONDBLCLK)
            {
                if (target != null)
                    ArmKeyboardInteraction(target, point);
                else
                    EndKeyboardInteraction("desktop surface has no wallpaper target");
            }
            else if (message == NativeMethods.WM_MBUTTONDOWN)
            {
                EndKeyboardInteraction("middle-button interaction");
            }

            if (target != null && message != NativeMethods.WM_RBUTTONDOWN && message != NativeMethods.WM_RBUTTONUP)
            {
                ForwardMouse(target, message, point, info.mouseData);
                return message == NativeMethods.WM_MOUSEMOVE
                    ? NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam)
                    : (IntPtr)1;
            }
        }
        else if (target != null && NativeMethods.IsDescendantOrSelf(underPointer, target.AppWindow.Hwnd))
        {
            if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONDBLCLK)
                ArmKeyboardInteraction(target, point);
            else if (message == NativeMethods.WM_MBUTTONDOWN)
                EndKeyboardInteraction("middle-button interaction");

            if (message != NativeMethods.WM_RBUTTONDOWN && message != NativeMethods.WM_RBUTTONUP)
            {
                ForwardMouse(target, message, point, info.mouseData);
                return message == NativeMethods.WM_MOUSEMOVE
                    ? NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam)
                    : (IntPtr)1;
            }
        }
        else if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
        {
            EndKeyboardInteraction("outside interaction");
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        int message = (int)wParam;
        if (message is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_KEYUP or
            NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_SYSKEYUP))
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        NativeMethods.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        int key = unchecked((int)info.vkCode);
        bool keyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

        bool altHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & unchecked((short)0x8000)) != 0;
        bool winHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & unchecked((short)0x8000)) != 0 ||
                       (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & unchecked((short)0x8000)) != 0;
        bool ctrlHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & unchecked((short)0x8000)) != 0;

        if (KeyboardInputPolicy.IsSystemOwnedKey(message, key, altHeld, winHeld, ctrlHeld))
        {
            if (KeyboardInputPolicy.EndsOwnership(message, key, altHeld, winHeld, ctrlHeld))
                EndKeyboardInteraction(GetSystemKeyReason(key));
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (_keyboardOwnership.State == KeyboardInputState.NoBackgroundKeyboardInput)
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        if (!TryValidateKeyboardInteraction(out IntPtr foreground, out KeyboardFocusKind focusKind))
        {
            EndKeyboardInteraction("foreground or renderer focus changed");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (_keyboardOwnership.State == KeyboardInputState.PendingBackgroundKeyboardInteraction)
        {
            if (!_keyboardOwnership.TryBegin(foreground, focusKind))
            {
                EndKeyboardInteraction("keyboard interaction could not begin");
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            Console.WriteLine($"[Input] Background keyboard interaction started: {FormatHwnd(_keyboardOwnership.Target)}");
        }

        IntPtr target = _keyboardOwnership.Target;
        if (!NativeMethods.IsWindow(target))
        {
            EndKeyboardInteraction("renderer target is no longer valid");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        PostKey(target, message, key, info);
        if (keyDown)
            _forwardedKeys[key] = info;
        else
            _forwardedKeys.Remove(key);

        return (IntPtr)1;
    }

    private void ArmKeyboardInteraction(WallpaperWindow window, NativeMethods.POINT screenPoint)
    {
        IntPtr target = ResolveInputWindow(window, screenPoint);
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            EndKeyboardInteraction("renderer target could not be resolved");
            return;
        }

        EndKeyboardInteraction("new background renderer interaction");
        _keyboardWindow = window;
        _keyboardOwnership.Arm(target);
        Console.WriteLine($"[Input] Background keyboard candidate armed: {FormatHwnd(target)}");
    }

    private void EndKeyboardInteraction(string reason)
    {
        IntPtr target = _keyboardOwnership.Target;
        if (target != IntPtr.Zero && NativeMethods.IsWindow(target))
        {
            foreach ((int key, NativeMethods.KBDLLHOOKSTRUCT info) in _forwardedKeys.ToArray())
                PostKey(target, NativeMethods.WM_KEYUP, key, info);
        }

        bool hadState = _keyboardOwnership.State != KeyboardInputState.NoBackgroundKeyboardInput;
        _forwardedKeys.Clear();
        _keyboardOwnership.End();
        _keyboardWindow = null;

        if (hadState)
            Console.WriteLine($"[Input] Background keyboard interaction ended: {reason}");
    }

    private bool TryValidateKeyboardInteraction(out IntPtr foreground, out KeyboardFocusKind focusKind)
    {
        foreground = NativeMethods.GetForegroundWindow();
        focusKind = KeyboardFocusKind.None;
        if (_keyboardWindow == null || foreground == IntPtr.Zero ||
            !NativeMethods.IsWindow(_keyboardWindow.AppWindow.Hwnd) ||
            !NativeMethods.IsWindow(_keyboardOwnership.Target) ||
            !NativeMethods.IsDescendantOrSelf(_keyboardOwnership.Target, _keyboardWindow.AppWindow.Hwnd) ||
            !_windows().Any(window => ReferenceEquals(window, _keyboardWindow)))
        {
            return false;
        }

        if (!IsBackgroundForegroundWindow(foreground, _keyboardOwnership.Target) ||
            !HasRendererFocus(_keyboardOwnership.Target))
        {
            return false;
        }

        focusKind = KeyboardFocusKind.RendererFallback;

        return _keyboardOwnership.State == KeyboardInputState.PendingBackgroundKeyboardInteraction ||
               _keyboardOwnership.IsValid(_keyboardOwnership.Target, foreground);
    }

    private static bool IsBackgroundForegroundWindow(IntPtr foreground, IntPtr target)
    {
        if (IsDesktopSurface(foreground))
            return true;

        return NativeMethods.IsDescendantOrSelf(foreground, target) ||
               NativeMethods.IsDescendantOrSelf(target, foreground);
    }

    private static bool HasRendererFocus(IntPtr target)
    {
        uint threadId = NativeMethods.GetWindowThreadProcessId(target, out _);
        if (threadId == 0)
            return false;

        var info = new NativeMethods.GUITHREADINFO
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref info))
            return false;

        return NativeMethods.IsDescendantOrSelf(info.HwndFocus, target);
    }

    private static string GetSystemKeyReason(int key) => key switch
    {
        NativeMethods.VK_LWIN or NativeMethods.VK_RWIN => "Windows key",
        NativeMethods.VK_LMENU or NativeMethods.VK_RMENU => "Alt key",
        NativeMethods.VK_TAB => "Alt+Tab",
        NativeMethods.VK_ESCAPE => "Escape",
        _ => "system key"
    };

    private void ForwardMouse(WallpaperWindow window, int message, NativeMethods.POINT screenPoint, uint mouseData)
    {
        IntPtr target = ResolveInputWindow(window, screenPoint);
        if (target == IntPtr.Zero)
            return;

        NativeMethods.POINT clientPoint = screenPoint;
        NativeMethods.ScreenToClient(target, ref clientPoint);
        IntPtr lParam = new((clientPoint.Y << 16) | (clientPoint.X & 0xFFFF));
        int keyState = GetMouseKeyState(message);
        IntPtr wParam = message is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL
            ? new IntPtr((unchecked((short)(mouseData >> 16)) << 16) | keyState)
            : new IntPtr(keyState);
        NativeMethods.PostMessageW(target, message, wParam, lParam);
    }

    private static void PostKey(IntPtr target, int message, int key, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        int flags = (int)info.flags;
        int lParam = 1 | ((int)info.scanCode << 16);
        if ((flags & 0x80) != 0 || message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            lParam |= unchecked((int)0xC0000000);
        if ((flags & 0x20) != 0)
            lParam |= 1 << 24;
        NativeMethods.PostMessageW(target, message, new IntPtr(key), new IntPtr(lParam));
    }

    private WallpaperWindow? FindWallpaperWindow(NativeMethods.POINT point)
    {
        return _windows()
            .Where(window => NativeMethods.IsWindow(window.AppWindow.Hwnd) && window.Bounds.Contains(point.X, point.Y))
            .OrderBy(window => window.Bounds.Width * (long)window.Bounds.Height)
            .FirstOrDefault();
    }

    private static IntPtr ResolveInputWindow(WallpaperWindow window, NativeMethods.POINT screenPoint)
    {
        IntPtr candidate = NativeMethods.WindowFromPoint(screenPoint);
        return candidate != IntPtr.Zero && NativeMethods.IsDescendantOrSelf(candidate, window.AppWindow.Hwnd)
            ? candidate
            : window.AppWindow.Hwnd;
    }

    private static int GetMouseKeyState(int message)
    {
        int state = 0;
        if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) state |= 0x0001;
        if ((NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0) state |= 0x0002;
        if ((NativeMethods.GetAsyncKeyState(0x04) & 0x8000) != 0) state |= 0x0010;
        if ((NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0) state |= 0x0004;
        if ((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) state |= 0x0008;

        if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK) state |= 0x0001;
        if (message is NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP) state |= 0x0002;
        if (message is NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP) state |= 0x0010;
        return state;
    }

    private static bool IsDesktopSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
            root = hwnd;

        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(root, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    private static bool IsDesktopIcon(IntPtr hwnd, NativeMethods.POINT point)
    {
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        if (className.ToString() != "SysListView32")
            return false;
        return NativeMethods.IsDesktopIconAt(hwnd, point);
    }

    private static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";

    private static void CloseDesktopMenus()
    {
        NativeMethods.PostMessageW(NativeMethods.GetShellWindow(), NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
        IntPtr menu = NativeMethods.FindWindowW("#32768", null);
        if (menu != IntPtr.Zero && NativeMethods.IsWindowVisible(menu))
        {
            NativeMethods.PostMessageW(menu, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.PostMessageW(menu, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_ESCAPE), IntPtr.Zero);
        }
    }
}
