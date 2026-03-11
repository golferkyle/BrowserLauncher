using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using log4net;
using log4net.Config;

var launcherBase = AppContext.BaseDirectory;
var launcherLogConfig = Path.Combine(launcherBase, "log4net.config");

try
{
    XmlConfigurator.Configure(new FileInfo(launcherLogConfig));
}
catch (Exception ex)
{
    Console.WriteLine($"Log4net config error: {ex.Message}");
    return;
}

var log = LogManager.GetLogger(typeof(Program));

// Read the JSON file directly to preserve localStorage structure
var appSettingsPath = Path.Combine(launcherBase, "appsettings.json");
var jsonText = File.ReadAllText(appSettingsPath);
var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var screens = config.GetSection("Screens").Get<List<ScreenConfig>>()!;
int delay = config.GetValue<int>("RestartDelaySeconds");
int logCleanupDays = config.GetValue<int?>("LogCleanupDays") ?? 10;
string logDirectory = config.GetValue<string>("LogDirectory") ?? "C:\\Temp\\BroswerHost";

var browserHostPath = ResolveBrowserHostPath(launcherBase, config);
if (string.IsNullOrWhiteSpace(browserHostPath) || !File.Exists(browserHostPath))
{
    log.Error("BrowserHost.exe could not be located. Set BrowserHostPath in appsettings.json or deploy BrowserHost next to Launcher.");
    Environment.Exit(1);
}

string browserHostDir = Path.GetDirectoryName(browserHostPath)!;

log.Info($"Config loaded: {screens.Count} screens, delay: {delay}s");
log.Info($"BrowserHost path: {browserHostPath}");

// ── Position-based monitor detection ──
// Sort physical monitors left-to-right (by X), then top-to-bottom (by Y).
// This ensures MonitorIndex 0 = leftmost, 1 = next, etc., regardless of
// how Windows enumerates DISPLAY devices.
var physicalScreens = Screen.AllScreens
    .OrderBy(s => s.Bounds.X)
    .ThenBy(s => s.Bounds.Y)
    .ToArray();

log.Info($"Detected {physicalScreens.Length} monitor(s) (sorted by position):");
for (int i = 0; i < physicalScreens.Length; i++)
{
    var s = physicalScreens[i];
    log.Info($"  Position {i}: {s.DeviceName}, Primary={s.Primary}, Bounds={s.Bounds}");
}

bool enableOnScreenKeyboard = config.GetValue<bool?>("EnableOnScreenKeyboard") ?? true;
bool enableOskFallback = config.GetValue<bool?>("EnableOskFallback") ?? false;
bool autoHideBottomBar = config.GetValue<bool?>("AutoHideBottomBar") ?? false;
bool alwaysAllowExit = config.GetValue<bool?>("AlwaysAllowExit") ?? false;
log.Info($"EnableOnScreenKeyboard: {enableOnScreenKeyboard}, EnableOskFallback: {enableOskFallback}, AutoHideBottomBar: {autoHideBottomBar}, AlwaysAllowExit: {alwaysAllowExit}");
int expectedCount = screens.Count;
int detectedCount = physicalScreens.Length;

bool monitorsMissing = detectedCount < expectedCount;
if (monitorsMissing)
{
    log.Warn($"Monitor mismatch: appsettings expects {expectedCount} screen(s) but only {detectedCount} detected.");
}

// Determine which screens cannot launch due to insufficient monitors
var skippedScreens = screens
    .Where(s => s.MonitorIndex < 0 || s.MonitorIndex >= detectedCount)
    .Select(s => s.MonitorIndex)
    .ToList();

// Write per-screen config files so BrowserHost can read them without fragile CLI arg escaping
var configDir = Path.Combine(Path.GetTempPath(), "BrowserHost");
Directory.CreateDirectory(configDir);

var screenIndex = 0;
foreach (var screen in screens)
{
    var positionalIndex = screen.MonitorIndex;

    if (positionalIndex < 0 || positionalIndex >= detectedCount)
    {
        log.Warn($"Skipping screen config MonitorIndex={positionalIndex}: only {detectedCount} monitor(s) detected. This browser will not launch.");
        screenIndex++;
        continue;
    }

    // If this screen requires all monitors and some are missing, skip it
    if (screen.RequireAllMonitors && monitorsMissing)
    {
        log.Warn($"Skipping MonitorIndex={positionalIndex}: RequireAllMonitors=true and only {detectedCount} of {expectedCount} monitor(s) detected.");
        screenIndex++;
        continue;
    }

    var physicalScreen = physicalScreens[positionalIndex];
    log.Info($"Mapping MonitorIndex {positionalIndex} -> {physicalScreen.DeviceName} at {physicalScreen.Bounds}");
    log.Info($"  URL: {screen.Url}, AllowExit: {screen.AllowExit}, ExitUrl: {screen.ExitUrl}, LogConsoleMessages: {screen.LogConsoleMessages}, DevTools: {screen.DevTools}");

    // Write the raw JSON element for this screen to a temp file.
    // This preserves the full LocalStorage structure without any CLI escaping issues.
    // We also inject the resolved screen bounds so BrowserHost doesn't need to do
    // its own monitor detection.
    var screenElement = jsonDoc.RootElement.GetProperty("Screens")[screenIndex];
    var screenJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(screenElement.GetRawText());
    screenJson!["ResolvedLeft"] = physicalScreen.Bounds.Left;
    screenJson!["ResolvedTop"] = physicalScreen.Bounds.Top;
    screenJson!["ResolvedWidth"] = physicalScreen.Bounds.Width;
    screenJson!["ResolvedHeight"] = physicalScreen.Bounds.Height;
    screenJson!["ResolvedDeviceName"] = physicalScreen.DeviceName;
    screenJson!["EnableOnScreenKeyboard"] = enableOnScreenKeyboard;
    screenJson!["EnableOskFallback"] = enableOskFallback;
    screenJson!["AutoHideBottomBar"] = autoHideBottomBar;
    screenJson!["AlwaysAllowExit"] = alwaysAllowExit;

    // If monitors are missing and this screen is launching anyway (RequireAllMonitors=false),
    // include the list of skipped screens so BrowserHost can show a warning.
    if (monitorsMissing && skippedScreens.Count > 0)
    {
        var skippedArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var idx in skippedScreens) skippedArray.Add(idx);
        screenJson!["SkippedMonitors"] = skippedArray;
        screenJson!["ExpectedMonitorCount"] = expectedCount;
        screenJson!["DetectedMonitorCount"] = detectedCount;
    }

    var configFilePath = Path.Combine(configDir, $"screen_config_{positionalIndex}.json");
    File.WriteAllText(configFilePath, screenJson.ToJsonString());
    log.Info($"Wrote screen config to {configFilePath}");
    screenIndex++;

    Task.Run(async () =>
    {
        try
        {
            await RunBrowser(screen, delay, browserHostPath, browserHostDir, configFilePath);
        }
        catch (Exception ex)
        {
            log.Error($"Error in task for monitor {screen.MonitorIndex}: {ex.Message}");
        }
    });
}

// Run log cleanup after launch tasks are kicked off to avoid slowing startup.
Task.Run(() => CleanupLogs(logDirectory, logCleanupDays, log));

await Task.Delay(Timeout.Infinite);

static async Task RunBrowser(ScreenConfig cfg, int delay, string exePath, string workingDir, string configFilePath)
{
    var log = LogManager.GetLogger(typeof(Program));
    while (true)
    {
        log.Info($"Launching BrowserHost for monitor {cfg.MonitorIndex}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{cfg.MonitorIndex} \"{configFilePath}\"",
                WorkingDirectory = workingDir,
                UseShellExecute = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        log.Info($"Process exited with code {process.ExitCode} for monitor {cfg.MonitorIndex}");
        if (process.ExitCode == 0)
        {
            log.Info("Manual exit detected, shutting down all browsers and launcher");
            foreach (var p in Process.GetProcessesByName("BrowserHost"))
            {
                try { p.Kill(); } catch { }
            }
            Environment.Exit(0);
        }
        if (process.ExitCode == 2)
        {
            log.Warn($"Monitor {cfg.MonitorIndex} not found. Stopping retries for this screen.");
            break;
        }
        await Task.Delay(TimeSpan.FromSeconds(delay));
    }
}

static string ResolveBrowserHostPath(string launcherBase, IConfiguration config)
{
    var configuredPath = config["BrowserHostPath"];
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
        if (Path.IsPathRooted(expanded))
        {
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }
        else
        {
            var relativeToBase = Path.GetFullPath(Path.Combine(launcherBase, expanded));
            if (File.Exists(relativeToBase))
            {
                return relativeToBase;
            }
        }
    }

    var candidates = new List<string>
    {
        Path.Combine(launcherBase, "BrowserHost", "BrowserHost.exe"),
        Path.Combine(launcherBase, "BrowserHost.exe"),
        Path.GetFullPath(Path.Combine(launcherBase, "..", "..", "..", "..", "BrowserHost", "bin", "Debug", "net8.0-windows", "BrowserHost.exe")),
        Path.GetFullPath(Path.Combine(launcherBase, "..", "..", "..", "..", "BrowserHost", "bin", "Release", "net8.0-windows", "BrowserHost.exe")),
        Path.GetFullPath(Path.Combine(launcherBase, "..", "..", "..", "..", "BrowserHost", "bin", "Release", "net8.0-windows", "publish", "BrowserHost.exe"))
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return string.Empty;
}

static void CleanupLogs(string logDirectory, int retentionDays, ILog log)
{
    try
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var expandedDir = Environment.ExpandEnvironmentVariables(logDirectory);
        if (!Directory.Exists(expandedDir))
        {
            return;
        }

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var patterns = new[] { "launcher.log*", "browserhost-*.log*" };

        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.GetFiles(expandedDir, pattern))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to delete log file '{file}': {ex.Message}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        log.Warn($"Log cleanup failed: {ex.Message}");
    }
}

record ScreenConfig(int MonitorIndex, string Url, bool AllowExit, string ExitUrl, bool LogConsoleMessages, bool DevTools, bool RequireAllMonitors = false);
