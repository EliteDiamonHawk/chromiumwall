using System.Drawing;
using BrowserWall;

namespace BrowserWall.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            (nameof(UrlBuilderPreservesQueryAndFragment), UrlBuilderPreservesQueryAndFragment),
            (nameof(UrlBuilderRejectsRelativeUrl), UrlBuilderRejectsRelativeUrl),
            (nameof(BrowserArgumentsUseArgumentListShape), BrowserArgumentsUseArgumentListShape),
            (nameof(LauncherCreatesUniqueTokens), LauncherCreatesUniqueTokens),
            (nameof(ExecutablePathComparisonIsCaseInsensitive), ExecutablePathComparisonIsCaseInsensitive),
            (nameof(RegistryTracksStatesAndHwndOwnership), RegistryTracksStatesAndHwndOwnership),
            (nameof(RegistryTracksIntentionalAndUnexpectedCloseIntent), RegistryTracksIntentionalAndUnexpectedCloseIntent),
            (nameof(CloseCheckRequiresAllWindows), CloseCheckRequiresAllWindows),
            (nameof(CloseCheckIntentionalClassificationIsAllOrNothing), CloseCheckIntentionalClassificationIsAllOrNothing),
            (nameof(PendingTimeoutBecomesUnexpectedClosed), PendingTimeoutBecomesUnexpectedClosed),
            (nameof(RegistryRediscoveryReleasesStaleHwnd), RegistryRediscoveryReleasesStaleHwnd),
            (nameof(RegistryRejectsDuplicateLogicalIds), RegistryRejectsDuplicateLogicalIds),
            (nameof(FinderRejectsAmbiguousMatches), FinderRejectsAmbiguousMatches),
            (nameof(FinderRejectsWrongExecutableAndOrdinaryWindows), FinderRejectsWrongExecutableAndOrdinaryWindows),
            (nameof(OptionsParseMonitorUrlOverrides), OptionsParseMonitorUrlOverrides),
            (nameof(TitlebarOffsetDefaultsToZero), TitlebarOffsetDefaultsToZero),
            (nameof(TitlebarOffsetParsesAsNonNegativePixels), TitlebarOffsetParsesAsNonNegativePixels),
            (nameof(TitlebarOffsetRejectsNegativeAndMalformedValues), TitlebarOffsetRejectsNegativeAndMalformedValues),
            (nameof(GeometryPreservesNegativeCoordinates), GeometryPreservesNegativeCoordinates),
            (nameof(NonClientInsetsAreDerivedFromMappedRectangles), NonClientInsetsAreDerivedFromMappedRectangles),
            (nameof(ClientBoundsExpandByMeasuredInsets), ClientBoundsExpandByMeasuredInsets),
            (nameof(ZeroInsetsPreserveClientBounds), ZeroInsetsPreserveClientBounds),
            (nameof(TopOverscanMovesUpAndPreservesBottom), TopOverscanMovesUpAndPreservesBottom),
            (nameof(TopOverscanStillCoversMonitor), TopOverscanStillCoversMonitor),
            (nameof(InvalidClientGeometryIsRejected), InvalidClientGeometryIsRejected),
            (nameof(BorderlessStyleTransformationIsIdempotent), BorderlessStyleTransformationIsIdempotent),
            (nameof(MonitorHostStyleClipsChildrenAndSiblings), MonitorHostStyleClipsChildrenAndSiblings),
            (nameof(ClientBoundsExpandInMonitorHostCoordinates), ClientBoundsExpandInMonitorHostCoordinates),
            (nameof(HelpTextUsesBrowserWall), HelpTextUsesBrowserWall)
        };

        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void UrlBuilderPreservesQueryAndFragment()
    {
        string result = UrlBuilder.AddLaunchIdentity(
            "https://example.test/wallpaper?theme=dark#scene",
            "monitor A",
            "token/value");

        AssertEqual(
            "https://example.test/wallpaper?theme=dark&window=monitor%20A&token=token%2Fvalue#scene",
            result);
    }

    private static void UrlBuilderRejectsRelativeUrl()
    {
        AssertThrows<ArgumentException>(() => UrlBuilder.AddLaunchIdentity("relative", "A", "token"));
    }

    private static void BrowserArgumentsUseArgumentListShape()
    {
        AppWindow appWindow = NewWindow("A", "abc");
        IReadOnlyList<string> arguments = BrowserLauncher.BuildArguments(
            appWindow,
            new[] { "--profile-directory=BrowserWall", "--disable-features=Some Feature" });

        AssertEqual("--profile-directory=BrowserWall", arguments[0]);
        AssertEqual("--disable-features=Some Feature", arguments[1]);
        AssertTrue(arguments[2].StartsWith("--app=https://example.test/?window=A&token=abc", StringComparison.Ordinal));
    }

    private static void LauncherCreatesUniqueTokens()
    {
        var launcher = new BrowserLauncher(CurrentExecutable());
        var requests = Enumerable.Range(0, 100)
            .Select(index => launcher.CreateRequest(new WallpaperWindowSpec($"window-{index}", "https://example.test", Rectangle.Empty, Rectangle.Empty), TimeSpan.FromSeconds(30)))
            .ToArray();

        AssertEqual(100, requests.Select(request => request.Token).Distinct(StringComparer.Ordinal).Count());
        AssertTrue(requests.All(request => request.ExpectedTitle.StartsWith("BrowserWall::", StringComparison.Ordinal)));
    }

    private static void ExecutablePathComparisonIsCaseInsensitive()
    {
        string path = CurrentExecutable();
        AssertTrue(ExecutablePathMatcher.PathsEqual(path, path.ToUpperInvariant()));
        AssertFalse(ExecutablePathMatcher.PathsEqual(path, Path.Combine(Path.GetDirectoryName(path)!, "different.exe")));
    }

    private static void RegistryTracksStatesAndHwndOwnership()
    {
        var registry = new WindowRegistry();
        AppWindow appWindow = NewWindow("A", "token-a");
        registry.Add(appWindow);
        registry.MarkFound(appWindow, new IntPtr(0x1234));
        AssertEqual(AppWindowState.Found, appWindow.State);
        AssertTrue(registry.IsHwndAssignedToAnother(appWindow.Hwnd, NewWindow("other", "other")));
        registry.MarkClosed(appWindow);
        AssertEqual(AppWindowState.Closed, appWindow.State);
        AssertEqual(WindowCloseIntent.Unexpected, appWindow.CloseIntent);
    }

    private static void RegistryTracksIntentionalAndUnexpectedCloseIntent()
    {
        var registry = new WindowRegistry();
        AppWindow intentional = NewWindow("intentional", "intentional-token");
        AppWindow unexpected = NewWindow("unexpected", "unexpected-token");
        registry.Add(intentional);
        registry.Add(unexpected);

        registry.MarkFound(intentional, new IntPtr(0x1001));
        registry.MarkIntentionalClose(intentional);
        registry.MarkClosed(intentional);

        registry.MarkFound(unexpected, new IntPtr(0x1002));
        registry.MarkClosed(unexpected);

        AssertEqual(WindowCloseIntent.Intentional, intentional.CloseIntent);
        AssertEqual(WindowCloseIntent.Unexpected, unexpected.CloseIntent);
    }

    private static void CloseCheckRequiresAllWindows()
    {
        var registry = new WindowRegistry();
        AppWindow closed = NewWindow("closed", "closed-token");
        AppWindow open = NewWindow("open", "open-token");
        registry.Add(closed);
        registry.Add(open);
        registry.MarkFound(closed, new IntPtr(0x2001));
        registry.MarkClosed(closed);
        registry.MarkFound(open, new IntPtr(0x2002));

        AssertFalse(CloseCheck.AllClosed(new[] { closed, open }));
        registry.MarkFailed(open, "test failure");
        AssertTrue(CloseCheck.AllClosed(new[] { closed, open }));
    }

    private static void CloseCheckIntentionalClassificationIsAllOrNothing()
    {
        AppWindow first = NewWindow("first", "first-token");
        AppWindow second = NewWindow("second", "second-token");
        var registry = new WindowRegistry();
        registry.Add(first);
        registry.Add(second);
        registry.MarkFound(first, new IntPtr(0x3001));
        registry.MarkFound(second, new IntPtr(0x3002));
        registry.MarkIntentionalClose(first);
        registry.MarkIntentionalClose(second);

        AssertTrue(CloseCheck.IsIntentional(new[] { first, second }));
        registry.MarkUnexpectedClose(second);
        AssertFalse(CloseCheck.IsIntentional(new[] { first, second }));
    }

    private static void PendingTimeoutBecomesUnexpectedClosed()
    {
        var registry = new WindowRegistry();
        AppWindow appWindow = NewWindow("pending", "pending-token");
        registry.Add(appWindow);

        registry.MarkClosed(appWindow);

        AssertEqual(AppWindowState.Closed, appWindow.State);
        AssertEqual(WindowCloseIntent.Unexpected, appWindow.CloseIntent);
        AssertTrue(CloseCheck.AllClosed(new[] { appWindow }));
    }

    private static void RegistryRejectsDuplicateLogicalIds()
    {
        var registry = new WindowRegistry();
        registry.Add(NewWindow("A", "one"));
        AssertThrows<InvalidOperationException>(() => registry.Add(NewWindow("A", "two")));
    }

    private static void RegistryRediscoveryReleasesStaleHwnd()
    {
        var registry = new WindowRegistry();
        AppWindow appWindow = NewWindow("A", "token-a");
        registry.Add(appWindow);
        IntPtr oldHwnd = new(0x1234);
        IntPtr newHwnd = new(0x5678);

        registry.MarkFound(appWindow, oldHwnd);
        registry.MarkPendingForRediscovery(appWindow, DateTimeOffset.UtcNow);
        AssertEqual(AppWindowState.Pending, appWindow.State);
        AssertFalse(registry.IsHwndAssignedToAnother(oldHwnd, NewWindow("other", "other")));

        registry.MarkFound(appWindow, newHwnd);
        AssertEqual(newHwnd, appWindow.Hwnd);
        AssertTrue(registry.IsHwndAssignedToAnother(newHwnd, NewWindow("other", "other")));
    }

    private static void GeometryPreservesNegativeCoordinates()
    {
        Rectangle monitor = new(-1920, -1200, 1920, 1200);
        Rectangle mapped = DesktopEmbedder.ToParentClient(monitor, new Point(-2000, -1300));

        AssertEqual(new Rectangle(80, 100, 1920, 1200), mapped);
        AssertEqual(1920, mapped.Width);
        AssertEqual(1200, mapped.Height);
    }

    private static void NonClientInsetsAreDerivedFromMappedRectangles()
    {
        Rectangle window = new(-8, -31, 1936, 1119);
        Rectangle client = new(0, 0, 1920, 1080);

        AssertTrue(DesktopEmbedder.TryCalculateNonClientInsets(window, client, out NonClientInsets insets));
        AssertEqual(new NonClientInsets(8, 31, 8, 8), insets);
    }

    private static void ClientBoundsExpandByMeasuredInsets()
    {
        Rectangle targetClient = new(-1920, -1200, 1920, 1200);
        Rectangle outer = DesktopEmbedder.CalculateOuterBounds(
            targetClient, new NonClientInsets(8, 31, 8, 8));

        AssertEqual(new Rectangle(-1928, -1231, 1936, 1239), outer);
    }

    private static void ZeroInsetsPreserveClientBounds()
    {
        Rectangle targetClient = new(10, 20, 300, 200);

        AssertEqual(targetClient, DesktopEmbedder.CalculateOuterBounds(targetClient, default));
    }

    private static void TopOverscanMovesUpAndPreservesBottom()
    {
        Rectangle original = new(100, 200, 1920, 1119);
        Rectangle adjusted = DesktopEmbedder.ApplyTopOverscan(original, 31);

        AssertEqual(new Rectangle(100, 169, 1920, 1150), adjusted);
        AssertEqual(original.Bottom, adjusted.Bottom);
    }

    private static void TopOverscanStillCoversMonitor()
    {
        Rectangle monitor = new(-1920, -1200, 1920, 1200);
        Rectangle client = DesktopEmbedder.ApplyTopOverscan(monitor, 31);

        AssertEqual(monitor, DesktopEmbedder.GetVisibleClientBounds(client, monitor));
        AssertEqual(monitor.Bottom, client.Bottom);
        AssertEqual(monitor.Top - 31, client.Top);
    }

    private static void InvalidClientGeometryIsRejected()
    {
        AssertFalse(DesktopEmbedder.TryCalculateNonClientInsets(
            new Rectangle(0, 0, 100, 100),
            new Rectangle(-1, 0, 100, 100),
            out _));
        AssertFalse(DesktopEmbedder.TryCalculateNonClientInsets(
            new Rectangle(0, 0, 100, 100),
            Rectangle.Empty,
            out _));
    }

    private static void BorderlessStyleTransformationIsIdempotent()
    {
        const long caption = 0x00C00000L;
        const long thickFrame = 0x00040000L;
        const long minimizeBox = 0x00020000L;
        const long maximizeBox = 0x00010000L;
        const long systemMenu = 0x00080000L;
        const long child = 0x40000000L;
        const long popup = unchecked((long)0x80000000);
        const long unrelated = 0x00000008L;
        long original = caption | thickFrame | minimizeBox | maximizeBox | systemMenu | popup | unrelated;

        long transformed = DesktopEmbedder.GetBorderlessChildStyle(original);
        long transformedAgain = DesktopEmbedder.GetBorderlessChildStyle(transformed);

        AssertFalse(DesktopEmbedder.HasNativeFrame(transformed));
        AssertTrue((transformed & child) != 0);
        AssertTrue((transformed & unrelated) != 0);
        AssertEqual(transformed, transformedAgain);
    }

    private static void MonitorHostStyleClipsChildrenAndSiblings()
    {
        const long child = 0x40000000L;
        const long visible = 0x10000000L;
        const long clipChildren = 0x02000000L;
        const long clipSiblings = 0x04000000L;

        long style = DesktopEmbedder.GetMonitorHostStyle();

        AssertTrue((style & child) != 0);
        AssertTrue((style & visible) != 0);
        AssertTrue((style & clipChildren) != 0);
        AssertTrue((style & clipSiblings) != 0);
    }

    private static void ClientBoundsExpandInMonitorHostCoordinates()
    {
        Rectangle monitorLocal = new(0, 0, 2560, 1440);
        Rectangle outer = DesktopEmbedder.CalculateOuterBounds(
            monitorLocal, new NonClientInsets(8, 31, 8, 8));

        AssertEqual(new Rectangle(-8, -31, 2576, 1479), outer);
    }

    private static void FinderRejectsAmbiguousMatches()
    {
        AppWindow appWindow = NewWindow("A", "token-a");
        var registry = new WindowRegistry();
        registry.Add(appWindow);
        var provider = new FakeSnapshotProvider(
            new WindowCandidate(new IntPtr(1), appWindow.ExpectedTitle, CurrentExecutable()),
            new WindowCandidate(new IntPtr(2), appWindow.ExpectedTitle, CurrentExecutable()));
        var finder = new WindowFinder(provider, CurrentExecutable());
        WindowSearchResult result = finder.Find(appWindow, registry);
        AssertEqual(WindowSearchStatus.Ambiguous, result.Status);
        AssertEqual(2, result.MatchCount);
    }

    private static void FinderRejectsWrongExecutableAndOrdinaryWindows()
    {
        AppWindow appWindow = NewWindow("A", "token-a");
        var registry = new WindowRegistry();
        registry.Add(appWindow);
        var provider = new FakeSnapshotProvider(
            new WindowCandidate(new IntPtr(1), "Normal browser", CurrentExecutable()),
            new WindowCandidate(new IntPtr(2), appWindow.ExpectedTitle, "C:\\other\\browser.exe"));
        var finder = new WindowFinder(provider, CurrentExecutable());
        AssertEqual(WindowSearchStatus.NotFound, finder.Find(appWindow, registry).Status);
    }

    private static void OptionsParseMonitorUrlOverrides()
    {
        string executable = CurrentExecutable();
        HostOptions options = HostOptions.Parse(new[]
        {
            "--browser", executable,
            "--url", "https://base.example",
            "--monitor-url", "2=https://monitor.example",
            "--mode", "all"
        });

        AssertEqual("https://monitor.example/", options.MonitorUrls[1]);
        AssertEqual(WallpaperMode.All, options.Mode);
    }

    private static void TitlebarOffsetDefaultsToZero()
    {
        HostOptions options = HostOptions.Parse(new[] { "--browser", CurrentExecutable() });

        AssertEqual(0, options.TitlebarOffset);
    }

    private static void TitlebarOffsetParsesAsNonNegativePixels()
    {
        HostOptions options = HostOptions.Parse(new[]
        {
            "--browser", CurrentExecutable(),
            "--titlebar-offset=31"
        });

        AssertEqual(31, options.TitlebarOffset);
    }

    private static void TitlebarOffsetRejectsNegativeAndMalformedValues()
    {
        AssertThrows<OptionException>(() => HostOptions.Parse(new[]
        {
            "--browser", CurrentExecutable(), "--titlebar-offset", "-1"
        }));
        AssertThrows<OptionException>(() => HostOptions.Parse(new[]
        {
            "--browser", CurrentExecutable(), "--titlebar-offset", "pixels"
        }));
    }

    private static void HelpTextUsesBrowserWall() =>
        AssertTrue(HostOptions.HelpText.Contains("BrowserWall", StringComparison.Ordinal));

    private static AppWindow NewWindow(string logicalId, string token) =>
        new(logicalId, token, $"https://example.test/?window={logicalId}&token={token}",
            new WallpaperWindowSpec(logicalId, "https://example.test", Rectangle.Empty, Rectangle.Empty),
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));

    private static string CurrentExecutable() =>
        Path.GetFullPath(Environment.ProcessPath ?? typeof(object).Assembly.Location);

    private sealed class FakeSnapshotProvider(params WindowCandidate[] candidates) : IWindowSnapshotProvider
    {
        public IReadOnlyList<WindowCandidate> GetCandidates() => candidates;
    }

    private static void AssertTrue(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void AssertFalse(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
