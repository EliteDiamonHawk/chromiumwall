using System.Text;

namespace ChromiumWall;

public sealed record WindowCandidate(IntPtr Hwnd, string Title, string ExecutablePath);

public interface IWindowSnapshotProvider
{
    IReadOnlyList<WindowCandidate> GetCandidates();
}

public sealed class WindowFinder
{
    private readonly IWindowSnapshotProvider _snapshotProvider;
    private readonly string _browserPath;
    private readonly bool _verbose;

    public WindowFinder(IWindowSnapshotProvider snapshotProvider, string browserPath, bool verbose = false)
    {
        _snapshotProvider = snapshotProvider;
        _browserPath = Path.GetFullPath(browserPath);
        _verbose = verbose;
    }

    public WindowSearchResult Find(AppWindow appWindow, WindowRegistry registry)
    {
        var matches = new List<WindowCandidate>();
        foreach (WindowCandidate candidate in _snapshotProvider.GetCandidates())
        {
            if (!string.Equals(candidate.Title, appWindow.ExpectedTitle, StringComparison.Ordinal))
                continue;
            if (registry.IsHwndAssignedToAnother(candidate.Hwnd, appWindow))
                continue;

            bool executableMatch = ExecutablePathMatcher.PathsEqual(candidate.ExecutablePath, _browserPath);
            if (_verbose)
            {
                Console.WriteLine($"[Finder] candidate HWND {FormatHwnd(candidate.Hwnd)}, title={candidate.Title}, process={candidate.ExecutablePath}, executableMatch={executableMatch}");
            }

            if (executableMatch)
                matches.Add(candidate);
        }

        return matches.Count switch
        {
            0 => WindowSearchResult.NotFound(),
            1 => WindowSearchResult.Found(matches[0].Hwnd),
            _ => WindowSearchResult.Ambiguous(matches.Count)
        };
    }

    public bool IsExpected(AppWindow appWindow)
    {
        if (appWindow.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(appWindow.Hwnd))
            return false;

        if (!string.Equals(NativeMethods.GetWindowTitle(appWindow.Hwnd), appWindow.ExpectedTitle, StringComparison.Ordinal))
            return false;

        return NativeMethods.TryGetProcessImagePath(appWindow.Hwnd, out string processPath) &&
               ExecutablePathMatcher.PathsEqual(processPath, _browserPath);
    }

    private static string FormatHwnd(IntPtr hwnd) => $"0x{hwnd.ToInt64():X}";
}

public enum WindowSearchStatus
{
    NotFound,
    Found,
    Ambiguous
}

public sealed record WindowSearchResult(WindowSearchStatus Status, IntPtr Hwnd, int MatchCount)
{
    public static WindowSearchResult NotFound() => new(WindowSearchStatus.NotFound, IntPtr.Zero, 0);
    public static WindowSearchResult Found(IntPtr hwnd) => new(WindowSearchStatus.Found, hwnd, 1);
    public static WindowSearchResult Ambiguous(int count) => new(WindowSearchStatus.Ambiguous, IntPtr.Zero, count);
}

public static class ExecutablePathMatcher
{
    public static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed class NativeWindowSnapshotProvider : IWindowSnapshotProvider
{
    private const int MaxTitleLength = 4096;

    public IReadOnlyList<WindowCandidate> GetCandidates()
    {
        var candidates = new List<WindowCandidate>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;

            string title = GetWindowTitle(hwnd);
            if (title.Length == 0 || !NativeMethods.TryGetProcessImagePath(hwnd, out string processPath))
                return true;

            candidates.Add(new WindowCandidate(hwnd, title, processPath));
            return true;
        }, IntPtr.Zero);
        return candidates;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var title = new StringBuilder(Math.Min(length + 1, MaxTitleLength));
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }
}
