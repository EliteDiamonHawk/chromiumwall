# ChromiumWall

ChromiumWall hosts a Chromium-compatible browser page as a Windows desktop wallpaper. It launches the browser externally in `--app` mode, identifies each resulting native window by a unique title token, then attaches only those windows behind desktop icons.

## Requirements

- Windows 10/11
- .NET 9 SDK
- Chromium-compatible browser executable

No browser embedding runtime or NuGet browser package is required.

## Build

```powershell
dotnet build ChromiumWall\ChromiumWall.sln -c Release
dotnet build ChromiumWall.Tests\ChromiumWall.Tests.csproj -c Release
dotnet run --project ChromiumWall.Tests\ChromiumWall.Tests.csproj -c Release
```

## Run

`--browser` is required. Without `--url`, ChromiumWall launches its copied `TestAssets\probe.html` page.

```powershell
ChromiumWall\bin\Release\net9.0-windows\ChromiumWall.exe `
  --browser "C:\Path\To\chrome.exe" `
  --mode all
```

Useful options:

- `--browser <path>`: browser executable.
- `--url <url>`: base HTTP, HTTPS, or file URL.
- `--monitor-url N=<url>`: per-monitor URL override; repeatable.
- `--mode span|all|single`: one virtual-screen window, one per monitor, or one selected monitor.
- `--monitor <number>`: 1-based monitor for `single` mode.
- `--browser-arg <arg>`: extra browser argument; repeatable.
- `--poll-ms <n>` and `--timeout-ms <n>`: native window discovery timing.
- `--titlebar-offset <n>`: move browser client content upward by `n` native pixels; default `0`.
- `--verbose`: candidate-window diagnostics.
- `--help`, `-h`: show help.

## Page title contract

ChromiumWall appends `window` and `token` query parameters to each URL. The page must set its top-level title to:

```javascript
document.title = `ChromiumWall::${token}`;
```

This title is the browser-independent launch-to-HWND handshake. The browser process ID is not used because Chromium may forward app launches into an already-running browser instance.

## Chromium

Recommended settings per browser

### Chrome

- enable "Continue running background apps when Google Chrome is closed" in <chrome://settings/system>
- use `--titlebar-offset 35` 

### Vivaldi

- enable "Continue running background apps when Vivaldi is closed" in <vivaldi:settings/system>

## Tray controls

The tray menu supports base URL changes, per-monitor URL changes, span/all/single monitor modes, reload-all, reload-one, status output, and exit. URL changes and reloads create and validate replacement windows before closing old tracked windows.

## Behavior

- WorkerW/Progman desktop embedding.
- Span, all-monitor, and selected-monitor modes.
- Borderless, taskbar-hidden wallpaper windows.
- Runtime URL changes and per-monitor links.
- Fresh-window reload and transactional replacement.
- Tray controls and CLI help.
- Mouse, button, double-click, movement, wheel, keyboard, modifier, and web-shortcut forwarding.
- Desktop icon selection, desktop context menu, critical system keys, and Alt+Tab pass-through.
- Initialization retries, discovery timeout, lifecycle tracking, and graceful shutdown.

Generic browser limitation: pages that do not implement the title contract cannot be correlated reliably and will time out.

## Future limitations

Explorer restart recovery, display hotplug/reconfiguration, and browser-specific focus/DPI quirks require later platform-specific work.

## License

MIT. See `LICENSE` and `Superswan\LICENSE`.
