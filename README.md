# BrowserHost

A .NET application for managing multiple browser instances across monitors with WebView2. The Launcher spawns and monitors BrowserHost instances on configured screens.

## Features

- Multi-monitor support with per-screen configuration
- WebView2-based browser hosting
- Automatic process restart on crashes
- Browser console logging to log4net
- localStorage injection for web applications
- Optional DevTools window
- Configurable exit buttons

## Requirements

- .NET 8.0 Runtime
- Windows 10/11
- WebView2 Runtime (automatically installed if needed)

## Configuration

Edit `appsettings.json` to configure screens:

```json
{
  "RestartDelaySeconds": 3,
  "Screens": [
    {
      "MonitorIndex": 0,
      "Url": "https://example.com",
      "AllowExit": true,
      "ExitUrl": "https://example.com",
      "LogConsoleMessages": false,
      "DevTools": false,
      "LocalStorage": {}
    }
  ]
}
```

### Configuration Options

- **MonitorIndex**: Monitor number (0-based)
- **Url**: Initial URL to load
- **AllowExit**: Show exit button when navigating to ExitUrl
- **ExitUrl**: URL that triggers the exit button visibility
- **LogConsoleMessages**: Log browser console messages to log4net
- **DevTools**: Open DevTools window on startup
- **LocalStorage**: Key/value pairs to inject into browser localStorage

## Running

```powershell
.\Published\Launcher.exe
```

## Building

```powershell
# Build both projects
dotnet build

# Publish release build
dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained false -o Published
dotnet publish BrowserHost/BrowserHost.csproj -c Release -r win-x64 --self-contained false -o Published/BrowserHost
```

## Project Structure

- **Launcher**: Console app that spawns and monitors BrowserHost instances
- **BrowserHost**: WPF app that hosts WebView2 on a specific monitor

## Logs

- `launcher.log`: Launcher process events
- `browserhost-{MonitorIndex}.log`: Per-monitor browser logs
