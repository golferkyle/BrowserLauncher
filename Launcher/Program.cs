using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.IO;
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

var screenIndex = 0;
foreach (var screen in screens)
{
    log.Info($"Starting task for monitor {screen.MonitorIndex}: {screen.Url}, AllowExit: {screen.AllowExit}, ExitUrl: {screen.ExitUrl}, LogConsoleMessages: {screen.LogConsoleMessages}, DevTools: {screen.DevTools}");
    
    // Get localStorage JSON from the raw JSON document
    var localStorageJson = "";
    var screenElement = jsonDoc.RootElement.GetProperty("Screens")[screenIndex];
    if (screenElement.TryGetProperty("LocalStorage", out var localStorageElement))
    {
        var localStorageText = localStorageElement.GetRawText();
        if (localStorageText != "{}")
        {
            localStorageJson = localStorageText;
        }
    }
    screenIndex++;
    
    Task.Run(async () => 
    {
        try
        {
            await RunBrowser(screen, delay, browserHostPath, browserHostDir, localStorageJson);
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

static async Task RunBrowser(ScreenConfig cfg, int delay, string exePath, string workingDir, string localStorageJson)
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
                Arguments = $"{cfg.MonitorIndex} \"{cfg.Url}\" {cfg.AllowExit} \"{cfg.ExitUrl}\" {cfg.LogConsoleMessages} \"{localStorageJson.Replace("\"", "\\\"")}\" {cfg.DevTools}",
                WorkingDirectory = workingDir,
                UseShellExecute = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        log.Info($"Process exited with code {process.ExitCode}, restarting in {delay}s");
        if (process.ExitCode == 0)
        {
            log.Info("Manual exit detected, shutting down all browsers and launcher");
            foreach (var p in Process.GetProcessesByName("BrowserHost"))
            {
                try { p.Kill(); } catch { }
            }
            Environment.Exit(0);
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

record ScreenConfig(int MonitorIndex, string Url, bool AllowExit, string ExitUrl, bool LogConsoleMessages, bool DevTools);
