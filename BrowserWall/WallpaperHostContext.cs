namespace BrowserWall;

public sealed class WallpaperHostContext : ApplicationContext
{
    private readonly HostOptions _options;
    private readonly BrowserLauncher _launcher;
    private readonly WindowFinder _finder;
    private readonly DesktopEmbedder _embedder = new();
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
    private ToolStripMenuItem? _spanItem;
    private ToolStripMenuItem? _allItem;

    public WallpaperHostContext(HostOptions options)
    {
        _options = options;
        _launcher = new BrowserLauncher(options.BrowserPath);
        _finder = new WindowFinder(new NativeWindowSnapshotProvider(), options.BrowserPath, options.Verbose);
        _baseUrl = options.Url;
        _monitorUrls = new Dictionary<int, string>(options.MonitorUrls);
        _mode = options.Mode;
        _selectedMonitor = options.SelectedMonitor;

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

        foreach (WallpaperWindow window in _windows.ToArray())
        {
            AppWindow appWindow = window.AppWindow;
            if (appWindow.State == AppWindowState.Found && !NativeMethods.IsWindow(appWindow.Hwnd))
            {
                _registry.MarkClosed(appWindow);
                Console.WriteLine($"[Finder] window closed: {appWindow.LogicalId} -> {FormatHwnd(appWindow.Hwnd)}");
            }
        }

        if (_pending == null && _windows.Count == 0 && !_exiting)
            ExitHost();
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
                    OnAppWindowFound(appWindow);
                }
                catch (Exception ex)
                {
                    pending.Registry.MarkFailed(appWindow, ex.Message);
                    Console.Error.WriteLine($"[Embed] failed for {appWindow.LogicalId}: {ex.Message}");
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

        if (pending.Registry.Items.All(window => window.State == AppWindowState.Found))
            CommitPending();
    }

    private void OnAppWindowFound(AppWindow appWindow)
    {
        // Future browser lifecycle extensions belong after this boundary.
        _embedder.Embed(appWindow);
    }

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
            _embedder.Close(oldWindow.AppWindow);

        var remaining = _windows.Where(window => !pending.Targets.Contains(window)).ToList();
        var mergedRegistry = new WindowRegistry();
        foreach (AppWindow oldWindow in _registry.Items.Where(window => pending.Targets.All(target => !ReferenceEquals(target.AppWindow, window))))
            mergedRegistry.Add(oldWindow);
        foreach (AppWindow newWindow in pending.Registry.Items)
            mergedRegistry.Add(newWindow);

        _windows = remaining.Concat(pending.Windows).ToList();
        _registry = mergedRegistry;
        _pending = null;
        BuildMenu();
        Console.WriteLine("[Replace] committed new browser windows.");
    }

    private void AbortPending()
    {
        ReplacementBatch pending = _pending!;
        foreach (AppWindow appWindow in pending.Registry.Items)
        {
            if (appWindow.State == AppWindowState.Found)
                _embedder.Close(appWindow);
        }

        _pending = null;
        Console.Error.WriteLine("[Replace] aborted; existing windows retained.");
        if (_windows.Count == 0)
            ExitHost();
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
            Console.WriteLine($"[Status] {appWindow.LogicalId}: {appWindow.State}, token={appWindow.Token}, HWND={FormatHwnd(appWindow.Hwnd)}, url={appWindow.Spec.Url}");
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
        _exiting = true;
        _timer.Stop();
        _input.Dispose();

        foreach (AppWindow appWindow in _registry.Items.Concat(_pending?.Registry.Items ?? Array.Empty<AppWindow>()))
        {
            if (appWindow.State == AppWindowState.Found)
                _embedder.Close(appWindow);
        }

        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        ExitThread();
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
