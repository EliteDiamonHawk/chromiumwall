namespace BrowserWall;

public sealed class WallpaperHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly BrowserLauncher _launcher;
    private readonly WindowFinder _finder;
    private readonly DesktopEmbedder _embedder;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly InputRouter _input;
    private readonly List<ToolStripMenuItem> _monitorItems = new();
    private readonly List<ToolStripMenuItem> _reloadItems = new();

    private WindowRegistry _registry = new();
    private List<WallpaperWindow> _windows = new();
    private ReplacementBatch? _pending;
    private string _baseUrl;
    private readonly Dictionary<int, string> _monitorUrls;
    private WallpaperMode _mode;
    private int _selectedMonitor;
    private bool _exiting;
    private bool _closeCheckLogged;
    private bool _allWindowsMissingLogged;
    private string _topologySignature;
    private ToolStripMenuItem? _spanItem;
    private ToolStripMenuItem? _allItem;

    public WallpaperHostContext(HostOptions options)
    {
        _options = options;
        _launcher = new BrowserLauncher(options.BrowserPath);
        _finder = new WindowFinder(new NativeWindowSnapshotProvider(), options.BrowserPath, options.Verbose);
        _embedder = new DesktopEmbedder(options.Verbose, options.TitlebarOffset);
        _baseUrl = options.Url;
        _monitorUrls = new Dictionary<int, string>(options.MonitorUrls);
        _mode = options.Mode;
        _selectedMonitor = options.SelectedMonitor;
        _topologySignature = GetTopologySignature();

        _menu = new ContextMenuStrip();
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "BrowserWall",
            Visible = true,
            ContextMenuStrip = _menu
        };
        BuildMenu();

        _input = new InputRouter(() => _windows);
        _timer = new System.Windows.Forms.Timer { Interval = options.PollIntervalMs };
        _timer.Tick += (_, _) => PollWindows();
        _timer.Start();

        BeginReplacement(options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), Array.Empty<WallpaperWindow>());
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _monitorItems.Clear();
        _reloadItems.Clear();

        _menu.Items.Add(new ToolStripMenuItem("Set base URL...", null, (_, _) => AskForBaseUrl()));

        var monitorUrlMenu = new ToolStripMenuItem("Set monitor URL...");
        for (int index = 0; index < Screen.AllScreens.Length; index++)
        {
            int monitor = index;
            monitorUrlMenu.DropDownItems.Add(new ToolStripMenuItem($"Monitor {index + 1}", null, (_, _) => AskForMonitorUrl(monitor)));
        }
        _menu.Items.Add(monitorUrlMenu);
        _menu.Items.Add(new ToolStripSeparator());

        _spanItem = new ToolStripMenuItem("Span (virtual screen)", null, (_, _) => SetMode(WallpaperMode.Span));
        _allItem = new ToolStripMenuItem("All monitors", null, (_, _) => SetMode(WallpaperMode.All));
        _menu.Items.Add(_spanItem);
        _menu.Items.Add(_allItem);

        var singleMenu = new ToolStripMenuItem("Single monitor");
        for (int index = 0; index < Screen.AllScreens.Length; index++)
        {
            int monitor = index;
            var item = new ToolStripMenuItem($"Monitor {index + 1}", null, (_, _) => SetSingleMonitor(monitor));
            _monitorItems.Add(item);
            singleMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(singleMenu);
        _menu.Items.Add(new ToolStripSeparator());

        var reloadMenu = new ToolStripMenuItem("Reload");
        reloadMenu.DropDownItems.Add(new ToolStripMenuItem("All windows", null, (_, _) => ReloadAll()));
        foreach (WallpaperWindow window in _windows)
        {
            WallpaperWindow selected = window;
            var item = new ToolStripMenuItem(selected.AppWindow.LogicalId, null, (_, _) => ReloadOne(selected));
            _reloadItems.Add(item);
            reloadMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(reloadMenu);
        _menu.Items.Add(new ToolStripMenuItem("Print status", null, (_, _) => PrintStatus()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitHost()));
        UpdateChecks();
    }

    private void PollWindows()
    {
        if (_pending != null)
            PollPending();

        if (_pending == null && !_exiting && GetTopologySignature() != _topologySignature)
        {
            _topologySignature = GetTopologySignature();
            BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);
            return;
        }

        foreach (WallpaperWindow window in _windows.ToArray())
        {
            AppWindow appWindow = window.AppWindow;
            if (appWindow.State is AppWindowState.Found or AppWindowState.Embedded)
            {
                if (!_finder.IsExpected(appWindow))
                {
                    _registry.MarkPendingForRediscovery(appWindow, DateTimeOffset.UtcNow);
                    Console.WriteLine($"[Finder] HWND lost or identity changed: {appWindow.LogicalId}; rediscovery started.");
                    continue;
                }

                try
                {
                    _embedder.Reconcile(appWindow);
                    _registry.MarkEmbedded(appWindow);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Embed] reconciliation failed for {appWindow.LogicalId}: {ex.Message}");
                }
            }
            else if (appWindow.State == AppWindowState.Pending)
            {
                PollRediscovery(appWindow);
            }
        }

        LogAllWindowsMissingIfNeeded();
        CheckForAllWindowsClosed();
    }

    private void PollRediscovery(AppWindow appWindow)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (appWindow.IsExpired(now))
        {
            _registry.MarkClosed(appWindow);
            Console.WriteLine($"[Finder] rediscovery timeout: {appWindow.LogicalId}");
            return;
        }

        WindowSearchResult result = _finder.Find(appWindow, _registry);
        if (result.Status != WindowSearchStatus.Found)
        {
            if (result.Status == WindowSearchStatus.Ambiguous)
                Console.Error.WriteLine($"[Finder] ambiguous rediscovery: {appWindow.LogicalId} ({result.MatchCount} windows)");
            return;
        }

        _registry.MarkFound(appWindow, result.Hwnd);
        try
        {
            OnAppWindowFound(appWindow, _registry);
            Console.WriteLine($"[Finder] rediscovered window: {appWindow.LogicalId} -> {FormatHwnd(result.Hwnd)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Embed] failed after rediscovery for {appWindow.LogicalId}: {ex.Message}");
            _registry.MarkPendingForRetry(appWindow);
        }
    }

    private void PollPending()
    {
        ReplacementBatch pending = _pending!;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (AppWindow appWindow in pending.Registry.Items.ToArray())
        {
            if (appWindow.State != AppWindowState.Pending)
                continue;

            if (appWindow.IsExpired(now))
            {
                pending.Registry.MarkFailed(appWindow, "window did not appear before timeout");
                Console.Error.WriteLine($"[Finder] timeout: {appWindow.LogicalId}");
                continue;
            }

            if (_options.Verbose)
                Console.WriteLine($"[Finder] searching for token {appWindow.Token}");

            WindowSearchResult result = _finder.Find(appWindow, pending.Registry);
            if (result.Status == WindowSearchStatus.Found)
            {
                pending.Registry.MarkFound(appWindow, result.Hwnd);
                Console.WriteLine($"[Finder] matched window: {appWindow.LogicalId} -> {FormatHwnd(result.Hwnd)}");
                try
                {
                    OnAppWindowFound(appWindow, pending.Registry);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Embed] failed for {appWindow.LogicalId}: {ex.Message}");
                    pending.Registry.MarkPendingForRetry(appWindow);
                }
            }
            else if (result.Status == WindowSearchStatus.Ambiguous)
            {
                pending.Registry.MarkFailed(appWindow, "multiple windows matched token");
                Console.Error.WriteLine($"[Finder] ambiguous match: {appWindow.LogicalId} ({result.MatchCount} windows)");
            }
        }

        if (pending.Registry.Items.Any(window => window.State == AppWindowState.Failed))
        {
            AbortPending();
            return;
        }

        if (pending.Registry.Items.All(window => window.State == AppWindowState.Embedded))
            CommitPending();
    }

    private void OnAppWindowFound(AppWindow appWindow, WindowRegistry registry)
    {
        _embedder.Embed(appWindow);
        registry.MarkEmbedded(appWindow);
    }

    private static string GetTopologySignature() =>
        string.Join("|", Screen.AllScreens.Select(screen => $"{screen.DeviceName}:{screen.Bounds}"));

    private void BeginReplacement(IReadOnlyList<WallpaperWindowSpec> specs, IReadOnlyList<WallpaperWindow> targets)
    {
        if (_pending != null)
        {
            Console.WriteLine("[Replace] request ignored while another replacement is pending.");
            return;
        }

        var registry = new WindowRegistry();
        var windows = new List<WallpaperWindow>();
        foreach (WallpaperWindowSpec spec in specs)
        {
            AppWindow appWindow = _launcher.CreateRequest(spec, _options.Timeout);
            registry.Add(appWindow);
            windows.Add(new WallpaperWindow(appWindow));

            Console.WriteLine($"[Browser] launching app window: {spec.LogicalId}");
            Console.WriteLine($"[Browser] token: {appWindow.Token}");
            Console.WriteLine($"[Browser] URL: {appWindow.Url}");
            try
            {
                _launcher.Start(appWindow, _options.BrowserArguments);
            }
            catch (Exception ex)
            {
                registry.MarkFailed(appWindow, ex.Message);
                Console.Error.WriteLine($"[Browser] launch failed for {spec.LogicalId}: {ex.Message}");
            }
        }

        _pending = new ReplacementBatch(registry, windows, targets.ToArray());
    }

    private void CommitPending()
    {
        ReplacementBatch pending = _pending!;
        foreach (WallpaperWindow oldWindow in pending.Targets)
        {
            _registry.MarkIntentionalClose(oldWindow.AppWindow);
            _embedder.Close(oldWindow.AppWindow);
        }

        var remaining = _windows.Where(window => !pending.Targets.Contains(window)).ToList();
        var mergedRegistry = new WindowRegistry();
        foreach (AppWindow oldWindow in _registry.Items.Where(window => pending.Targets.All(target => !ReferenceEquals(target.AppWindow, window))))
            mergedRegistry.Add(oldWindow);
        foreach (AppWindow newWindow in pending.Registry.Items)
            mergedRegistry.Add(newWindow);

        _windows = remaining.Concat(pending.Windows).ToList();
        _registry = mergedRegistry;
        _pending = null;
        _closeCheckLogged = false;
        _allWindowsMissingLogged = false;
        BuildMenu();
        Console.WriteLine("[Replace] committed new browser windows.");
    }

    private void AbortPending()
    {
        ReplacementBatch pending = _pending!;
        foreach (AppWindow appWindow in pending.Registry.Items)
        {
            if (appWindow.State is AppWindowState.Found or AppWindowState.Embedded)
            {
                pending.Registry.MarkIntentionalClose(appWindow);
                _embedder.Close(appWindow);
            }
        }

        _pending = null;
        Console.Error.WriteLine("[Replace] aborted; existing windows retained; host remains running.");
    }

    private void SetMode(WallpaperMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);
        UpdateChecks();
    }

    private void SetSingleMonitor(int monitor)
    {
        _selectedMonitor = monitor;
        _mode = WallpaperMode.Single;
        BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);
        UpdateChecks();
    }

    private void AskForBaseUrl()
    {
        using var input = new InputBox("Set base URL", "Enter URL:", _baseUrl);
        if (input.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            _baseUrl = UrlBuilder.Normalize(input.InputText);
            BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "BrowserWall - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AskForMonitorUrl(int monitor)
    {
        string current = _monitorUrls.TryGetValue(monitor, out string? url) ? url : _baseUrl;
        using var input = new InputBox($"Set monitor {monitor + 1} URL", "Enter URL:", current);
        if (input.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            _monitorUrls[monitor] = UrlBuilder.Normalize(input.InputText);
            BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "BrowserWall - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReloadAll() =>
        BeginReplacement(_options.ResolveWindowSpecs(_baseUrl, _mode, _selectedMonitor, _monitorUrls), _windows);

    private void ReloadOne(WallpaperWindow window)
    {
        var spec = new WallpaperWindowSpec(
            window.AppWindow.LogicalId,
            window.AppWindow.Spec.Url,
            window.AppWindow.Spec.Bounds,
            window.AppWindow.Spec.VirtualBounds);
        BeginReplacement(new[] { spec }, new[] { window });
    }

    private void PrintStatus()
    {
        foreach (AppWindow appWindow in _registry.Items)
        {
            Console.WriteLine($"[Status] {appWindow.LogicalId}: {appWindow.State}, token={appWindow.Token}, " +
                $"HWND={FormatHwnd(appWindow.Hwnd)}, host={FormatHwnd(appWindow.MonitorHostHwnd)}, url={appWindow.Spec.Url}");
        }
    }

    private void UpdateChecks()
    {
        if (_spanItem != null) _spanItem.Checked = _mode == WallpaperMode.Span;
        if (_allItem != null) _allItem.Checked = _mode == WallpaperMode.All;
        for (int index = 0; index < _monitorItems.Count; index++)
            _monitorItems[index].Checked = _mode == WallpaperMode.Single && index == _selectedMonitor;
    }

    private void ExitHost()
    {
        if (_exiting)
            return;

        foreach (AppWindow appWindow in _registry.Items.Concat(_pending?.Registry.Items ?? Array.Empty<AppWindow>()))
        {
            if (appWindow.State is AppWindowState.Found or AppWindowState.Embedded)
            {
                _registry.MarkIntentionalClose(appWindow);
                _embedder.Close(appWindow);
                appWindow.State = AppWindowState.Closed;
            }
        }

        CheckForAllWindowsClosed();

        _exiting = true;
        _timer.Stop();
        _input.Dispose();

        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        ExitThread();
    }

    private void CheckForAllWindowsClosed()
    {
        if (_closeCheckLogged || !CloseCheck.AllClosed(_windows.Select(window => window.AppWindow)))
            return;

        bool intentional = CloseCheck.IsIntentional(_windows.Select(window => window.AppWindow));
        Console.WriteLine($"[CloseCheck] all tracked windows closed; intentional={intentional.ToString().ToLowerInvariant()}");
        _closeCheckLogged = true;

    }

    private void LogAllWindowsMissingIfNeeded()
    {
        bool allMissing = _windows.Count > 0 && _windows.All(window =>
            window.AppWindow.State == AppWindowState.Pending);

        if (!allMissing)
        {
            _allWindowsMissingLogged = false;
            return;
        }

        if (!_allWindowsMissingLogged)
        {
            Console.WriteLine("[CloseCheck] all tracked windows missing; waiting for rediscovery timeout.");
            _allWindowsMissingLogged = true;
        }
    }

    private static string FormatHwnd(IntPtr hwnd) => hwnd == IntPtr.Zero ? "<none>" : $"0x{hwnd.ToInt64():X}";

    private sealed record ReplacementBatch(WindowRegistry Registry, List<WallpaperWindow> Windows, WallpaperWindow[] Targets);
}

public sealed class InputBox : Form
{
    private readonly TextBox _textBox;

    public InputBox(string title, string prompt, string defaultText)
    {
        Text = title;
        Size = new Size(420, 170);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var label = new Label { Left = 10, Top = 10, Width = 385, Text = prompt };
        _textBox = new TextBox { Left = 10, Top = 38, Width = 385, Text = defaultText };
        var ok = new Button { Text = "OK", Left = 230, Top = 85, Width = 78, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 317, Top = 85, Width = 78, DialogResult = DialogResult.Cancel };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(label);
        Controls.Add(_textBox);
        Controls.Add(ok);
        Controls.Add(cancel);
    }

    public string InputText => _textBox.Text;
}
