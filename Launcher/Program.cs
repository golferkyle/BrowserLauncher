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
log.Debug($"Read appsettings.json: {jsonText.Length} chars");
var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var screens = config.GetSection("Screens").Get<List<ScreenConfig>>()!;
int delay = config.GetValue<int>("RestartDelaySeconds");

string browserHostPath = Path.Combine(launcherBase, "BrowserHost", "BrowserHost.exe");
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
            log.Debug($"Monitor {screen.MonitorIndex} localStorage JSON: {localStorageJson}");
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
        log.Debug($"Process started, PID: {process.Id}");
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

record ScreenConfig(int MonitorIndex, string Url, bool AllowExit, string ExitUrl, bool LogConsoleMessages, bool DevTools);
