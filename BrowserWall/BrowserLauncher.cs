using System.Diagnostics;

namespace BrowserWall;

public sealed class BrowserLauncher
{
    public BrowserLauncher(string browserPath)
    {
        BrowserPath = Path.GetFullPath(browserPath);
        if (!File.Exists(BrowserPath))
            throw new FileNotFoundException("Browser executable does not exist.", BrowserPath);
    }

    public string BrowserPath { get; }

    public AppWindow CreateRequest(WallpaperWindowSpec spec, TimeSpan timeout)
    {
        string token = Guid.NewGuid().ToString("N");
        string url = UrlBuilder.AddLaunchIdentity(spec.Url, spec.LogicalId, token);
        return new AppWindow(spec.LogicalId, token, url, spec, DateTimeOffset.UtcNow, timeout);
    }

    public void Start(AppWindow appWindow, IReadOnlyList<string> browserArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = BrowserPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(BrowserPath) ?? AppContext.BaseDirectory
        };

        foreach (string argument in BuildArguments(appWindow, browserArguments))
            startInfo.ArgumentList.Add(argument);

        using Process? process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Browser process could not be started.");
    }

    public static IReadOnlyList<string> BuildArguments(AppWindow appWindow, IReadOnlyList<string> browserArguments)
    {
        var arguments = new List<string>(browserArguments.Count + 1);
        arguments.AddRange(browserArguments);
        arguments.Add($"--app={appWindow.Url}");
        return arguments;
    }
}

public sealed class AppWindow
{
    public AppWindow(
        string logicalId,
        string token,
        string url,
        WallpaperWindowSpec spec,
        DateTimeOffset startedAt,
        TimeSpan timeout)
    {
        LogicalId = logicalId;
        Token = token;
        Url = url;
        Spec = spec;
        StartedAt = startedAt;
        DiscoveryTimeout = timeout;
        Deadline = startedAt + timeout;
    }

    public string LogicalId { get; }
    public string Token { get; }
    public string Url { get; }
    public WallpaperWindowSpec Spec { get; }
    public string ExpectedTitle => $"BrowserWall::{Token}";
    public DateTimeOffset StartedAt { get; }
    public TimeSpan DiscoveryTimeout { get; }
    public DateTimeOffset Deadline { get; internal set; }
    public IntPtr Hwnd { get; internal set; }
    public IntPtr MonitorHostHwnd { get; internal set; }
    public AppWindowState State { get; internal set; } = AppWindowState.Pending;
    public string? FailureReason { get; internal set; }
    public IntPtr OriginalHwnd { get; internal set; }
    public NativeWindowSnapshot? OriginalNativeState { get; internal set; }

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;

    internal void ResetDiscovery(DateTimeOffset now)
    {
        Hwnd = IntPtr.Zero;
        OriginalHwnd = IntPtr.Zero;
        OriginalNativeState = null;
        FailureReason = null;
        Deadline = now + DiscoveryTimeout;
        State = AppWindowState.Pending;
    }
}

public sealed record NativeWindowSnapshot(long Style, long ExtendedStyle, IntPtr Parent, Rectangle Bounds);

public enum AppWindowState
{
    Pending,
    Found,
    Embedded,
    Closed,
    Failed
}

public static class UrlBuilder
{
    public static string Normalize(string value)
    {
        string url = value.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = $"https://{url}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeFile))
        {
            throw new OptionException($"Invalid URL: {url}");
        }

        return uri.AbsoluteUri;
    }

    public static string AddLaunchIdentity(string baseUrl, string logicalId, string token)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("URL must be absolute.", nameof(baseUrl));

        var builder = new UriBuilder(uri);
        string existingQuery = builder.Query.TrimStart('?');
        string launchQuery = $"window={Uri.EscapeDataString(logicalId)}&token={Uri.EscapeDataString(token)}";
        builder.Query = string.IsNullOrEmpty(existingQuery) ? launchQuery : $"{existingQuery}&{launchQuery}";
        return builder.Uri.AbsoluteUri;
    }
}
