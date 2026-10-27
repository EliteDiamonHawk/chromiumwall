using System.Globalization;

namespace BrowserWall;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            HostOptions options = HostOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(HostOptions.HelpText);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new WallpaperHostContext(options));
            return 0;
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            return 1;
        }
    }
}

public enum WallpaperMode
{
    Span,
    All,
    Single
}

public sealed record WallpaperWindowSpec(string LogicalId, string Url, Rectangle Bounds, Rectangle VirtualBounds);

public sealed class HostOptions
{
    private HostOptions() { }

    public string BrowserPath { get; private init; } = "";
    public string Url { get; private init; } = "";
    public IReadOnlyList<string> BrowserArguments { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExplicitWindowIds { get; private init; } = Array.Empty<string>();
    public IReadOnlyDictionary<int, string> MonitorUrls { get; private init; } = new Dictionary<int, string>();
    public WallpaperMode Mode { get; private init; } = WallpaperMode.Span;
    public int SelectedMonitor { get; private init; }
    public int PollIntervalMs { get; private init; } = 250;
    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(30);
    public int TitlebarOffset { get; private init; }
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static HostOptions Parse(string[] args)
    {
        string? browserPath = null;
        string url = DefaultProbeUrl();
        var browserArguments = new List<string>();
        var windowIds = new List<string>();
        var monitorUrls = new Dictionary<int, string>();
        WallpaperMode mode = WallpaperMode.Span;
        int selectedMonitor = 0;
        int pollMs = 250;
        int timeoutMs = 30_000;
        int titlebarOffset = 0;
        bool verbose = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
                return new HostOptions { ShowHelp = true };

            if (argument == "--verbose")
            {
                verbose = true;
                continue;
            }

            string option = argument;
            string? inlineValue = null;
            int equalsIndex = argument.IndexOf('=');
            if (equalsIndex > 0)
            {
                option = argument[..equalsIndex];
                inlineValue = argument[(equalsIndex + 1)..];
            }

            switch (option)
            {
                case "--browser":
                    browserPath = NextValue(args, ref index, option, inlineValue);
                    break;
                case "--url":
                    url = UrlBuilder.Normalize(NextValue(args, ref index, option, inlineValue));
                    break;
                case "--browser-arg":
                    browserArguments.Add(NextValue(args, ref index, option, inlineValue, true));
                    break;
                case "--window":
                {
                    string id = NextValue(args, ref index, option, inlineValue).Trim();
                    if (id.Length == 0)
                        throw new OptionException("--window requires a non-empty logical ID.");
                    windowIds.Add(id);
                    break;
                }
                case "--monitor-url":
                {
                    string value = NextValue(args, ref index, option, inlineValue);
                    int separator = value.IndexOf('=');
                    if (separator <= 0)
                        throw new OptionException("--monitor-url requires <1-based-monitor>=<url>.");

                    int monitor = ParsePositiveInt(value[..separator], option) - 1;
                    monitorUrls[monitor] = UrlBuilder.Normalize(value[(separator + 1)..]);
                    break;
                }
                case "--mode":
                    mode = NextValue(args, ref index, option, inlineValue).ToLowerInvariant() switch
                    {
                        "span" => WallpaperMode.Span,
                        "all" => WallpaperMode.All,
                        "single" => WallpaperMode.Single,
                        _ => throw new OptionException("--mode must be span, all, or single.")
                    };
                    break;
                case "--monitor":
                    selectedMonitor = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option) - 1;
                    break;
                case "--poll-ms":
                    pollMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--timeout-ms":
                    timeoutMs = ParsePositiveInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                case "--titlebar-offset":
                    titlebarOffset = ParseNonNegativeInt(NextValue(args, ref index, option, inlineValue), option);
                    break;
                default:
                    throw new OptionException($"Unknown argument: {argument}");
            }
        }

        if (string.IsNullOrWhiteSpace(browserPath))
            throw new OptionException("--browser <path> is required.");

        browserPath = Path.GetFullPath(browserPath);
        if (!File.Exists(browserPath))
            throw new OptionException($"Browser executable does not exist: {browserPath}");

        return new HostOptions
        {
            BrowserPath = browserPath,
            Url = url,
            BrowserArguments = browserArguments,
            ExplicitWindowIds = windowIds,
            MonitorUrls = monitorUrls,
            Mode = mode,
            SelectedMonitor = selectedMonitor,
            PollIntervalMs = pollMs,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            TitlebarOffset = titlebarOffset,
            Verbose = verbose
        };
    }

    public IReadOnlyList<WallpaperWindowSpec> ResolveWindowSpecs(
        string? baseUrl = null,
        WallpaperMode? mode = null,
        int? selectedMonitor = null,
        IReadOnlyDictionary<int, string>? monitorUrls = null)
    {
        string resolvedUrl = baseUrl ?? Url;
        WallpaperMode resolvedMode = mode ?? Mode;
        int resolvedMonitor = selectedMonitor ?? SelectedMonitor;
        IReadOnlyDictionary<int, string> resolvedOverrides = monitorUrls ?? MonitorUrls;
        Screen[] screens = Screen.AllScreens;
        Rectangle virtualBounds = SystemInformation.VirtualScreen;

        if (screens.Length == 0)
            return new[] { new WallpaperWindowSpec("virtual-screen", resolvedUrl, virtualBounds, virtualBounds) };

        if (ExplicitWindowIds.Count > 0)
        {
            return ExplicitWindowIds
                .Select(id => new WallpaperWindowSpec(id, resolvedUrl, virtualBounds, virtualBounds))
                .ToArray();
        }

        return resolvedMode switch
        {
            WallpaperMode.Span => new[] { new WallpaperWindowSpec("virtual-screen", resolvedUrl, virtualBounds, virtualBounds) },
            WallpaperMode.All => screens.Select((screen, index) => new WallpaperWindowSpec(
                $"monitor-{index + 1}",
                resolvedOverrides.TryGetValue(index, out string? overrideUrl) ? overrideUrl : resolvedUrl,
                screen.Bounds,
                virtualBounds)).ToArray(),
            WallpaperMode.Single => new[]
            {
                CreateSingleSpec(screens, Math.Clamp(resolvedMonitor, 0, screens.Length - 1), resolvedUrl, resolvedOverrides, virtualBounds)
            },
            _ => throw new InvalidOperationException("Unknown wallpaper mode.")
        };
    }

    private static WallpaperWindowSpec CreateSingleSpec(
        Screen[] screens,
        int index,
        string baseUrl,
        IReadOnlyDictionary<int, string> overrides,
        Rectangle virtualBounds)
    {
        string url = overrides.TryGetValue(index, out string? overrideUrl) ? overrideUrl : baseUrl;
        return new WallpaperWindowSpec($"monitor-{index + 1}", url, screens[index].Bounds, virtualBounds);
    }

    private static string NextValue(string[] args, ref int index, string option, string? inlineValue, bool allowOptionLikeValue = false)
    {
        if (inlineValue != null)
            return inlineValue;
        if (++index >= args.Length || (!allowOptionLikeValue && args[index].StartsWith("--", StringComparison.Ordinal)))
            throw new OptionException($"{option} requires a value.");
        return args[index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new OptionException($"{option} requires a positive integer.");
        return parsed;
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
            throw new OptionException($"{option} requires a non-negative integer.");
        return parsed;
    }

    private static string DefaultProbeUrl() =>
        new Uri(Path.Combine(AppContext.BaseDirectory, "TestAssets", "probe.html")).AbsoluteUri;

    public const string HelpText = """
        BrowserWall - Chromium live wallpaper host

        Usage:
          BrowserWall.exe --browser <path> [options]

        Options:
          --browser <path>       Chromium-compatible executable. Required.
          --url <url>             HTTP, HTTPS, or file URL. Defaults to probe.html.
          --monitor-url N=<url>   URL override for 1-based monitor N. Repeatable.
          --browser-arg <arg>    Additional browser argument. Repeatable.
          --window <id>           Explicit logical window ID. Repeatable.
          --mode <mode>           span, all, or single. Default: span.
          --monitor <number>      1-based monitor for single mode. Default: 1.
          --poll-ms <n>           Finder polling interval. Default: 250.
          --timeout-ms <n>        Window discovery timeout. Default: 30000.
          --titlebar-offset <n>   Move browser client content upward by n pixels. Default: 0.
          --verbose               Log candidate windows during discovery.
          --help, -h              Show this help.

        Pages must set document.title to BrowserWall::<token>.
        """;
}

public sealed class OptionException : Exception
{
    public OptionException(string message) : base(message) { }
}
