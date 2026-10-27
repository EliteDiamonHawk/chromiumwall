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
            (nameof(RegistryRejectsDuplicateLogicalIds), RegistryRejectsDuplicateLogicalIds),
            (nameof(FinderRejectsAmbiguousMatches), FinderRejectsAmbiguousMatches),
            (nameof(FinderRejectsWrongExecutableAndOrdinaryWindows), FinderRejectsWrongExecutableAndOrdinaryWindows),
            (nameof(OptionsParseMonitorUrlOverrides), OptionsParseMonitorUrlOverrides),
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
    }

    private static void RegistryRejectsDuplicateLogicalIds()
    {
        var registry = new WindowRegistry();
        registry.Add(NewWindow("A", "one"));
        AssertThrows<InvalidOperationException>(() => registry.Add(NewWindow("A", "two")));
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
