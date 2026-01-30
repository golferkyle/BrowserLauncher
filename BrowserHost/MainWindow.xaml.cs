using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;
using System.IO;
using log4net;
using log4net.Config;

namespace BrowserHost
{
    public partial class MainWindow : Window
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(MainWindow));
        private bool allowExit;
        private string exitUrl = string.Empty;
        private readonly int monitorIndex = -1;
        private bool logConsoleMessages = false;
        private string localStorageJson = string.Empty;
        private bool devTools = false;

        public MainWindow()
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && int.TryParse(args[1], out var parsedIndex))
            {
                monitorIndex = parsedIndex;
                log4net.GlobalContext.Properties["MonitorIndex"] = monitorIndex;
            }
            else
            {
                log4net.GlobalContext.Properties["MonitorIndex"] = "unknown";
            }

            var baseDir = AppContext.BaseDirectory;
            var logConfig = Path.Combine(baseDir, "log4net.config");
            XmlConfigurator.Configure(new FileInfo(logConfig));
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var args = Environment.GetCommandLineArgs();
            log.Info($"Starting BrowserHost with args: {string.Join(" ", args)}");
            try
            {
                if (args.Length < 7)
                {
                    log.Info("Not enough args, shutting down");
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                if (monitorIndex < 0)
                {
                    log.Info("Monitor index missing or invalid, shutting down");
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                string url = args[2];
                allowExit = bool.Parse(args[3]);
                exitUrl = args[4];
                logConsoleMessages = bool.Parse(args[5]);
                if (args.Length > 6)
                {
                    localStorageJson = args[6];
                }
                if (args.Length > 7)
                {
                    devTools = bool.Parse(args[7]);
                }
                if (string.IsNullOrWhiteSpace(exitUrl))
                {
                    exitUrl = url;
                    log.Info($"ExitUrl not provided; using initial URL as exit target: {exitUrl}");
                }
                log.Info($"Monitor: {monitorIndex}, URL: {url}, AllowExit: {allowExit}, ExitUrl: {exitUrl}, LogConsoleMessages: {logConsoleMessages}, DevTools: {devTools}, LocalStorage: {(string.IsNullOrWhiteSpace(localStorageJson) ? "none" : "configured")}");

                var screens = Screen.AllScreens;
                log.Info($"Available screens: {screens.Length}");
                if (monitorIndex >= screens.Length)
                {
                    log.Info("Monitor index out of range, shutting down");
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                var screen = screens[monitorIndex];
                log.Info($"Screen bounds: {screen.Bounds}");

                Left = screen.Bounds.Left;
                Top = screen.Bounds.Top;
                Width = screen.Bounds.Width;
                Height = screen.Bounds.Height;

                // Position popup at bottom left of this window
                ExitPopup.HorizontalOffset = Left + 10;
                ExitPopup.VerticalOffset = Top + Height - 50;

                log.Info("Ensuring WebView2");
                await WebView.EnsureCoreWebView2Async();
                log.Info("WebView2 ensured");

                WebView.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
                WebView.SourceChanged += WebView_SourceChanged;

                // Inject localStorage key/value pairs
                if (!string.IsNullOrWhiteSpace(localStorageJson))
                {
                    try
                    {
                        var localStorageData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(localStorageJson);
                        if (localStorageData != null && localStorageData.Count > 0)
                        {
                            var scriptLines = new System.Text.StringBuilder();
                            foreach (var kvp in localStorageData)
                            {
                                var value = kvp.Value.GetRawText();
                                
                                log.Debug($"LocalStorage key: {kvp.Key}, raw value: {value}");
                                
                                var escapedValue = value.Replace("\\", "\\\\").Replace("'", "\\'")
                                                        .Replace("\n", "\\n").Replace("\r", "\\r");
                                scriptLines.AppendLine($"localStorage.setItem('{kvp.Key}', '{escapedValue}');");
                            }
                            
                            var script = scriptLines.ToString();
                            log.Debug($"Executing localStorage script: {script}");
                            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                            log.Info($"LocalStorage injected: {localStorageData.Count} key(s): {string.Join(", ", localStorageData.Keys)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Failed to parse or inject localStorage: {ex.Message}");
                    }
                }

                // Open DevTools if requested
                if (devTools)
                {
                    WebView.CoreWebView2.OpenDevToolsWindow();
                    log.Info("DevTools window opened");
                }

                if (logConsoleMessages)
                {
                    WebView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;
                    
                    // Inject script to capture all console messages
                    await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                        (function() {
                            const originalLog = console.log;
                            const originalError = console.error;
                            const originalWarn = console.warn;
                            const originalInfo = console.info;
                            const originalDebug = console.debug;
                            
                            function serializeArg(arg) {
                                try {
                                    if (arg === null) return 'null';
                                    if (arg === undefined) return 'undefined';
                                    if (typeof arg === 'string') return arg;
                                    if (typeof arg === 'number' || typeof arg === 'boolean') return String(arg);
                                    if (arg instanceof Error) return arg.stack || arg.message;
                                    if (typeof arg === 'function') return arg.toString();
                                    // Try to stringify objects/arrays
                                    return JSON.stringify(arg, (key, value) => {
                                        if (typeof value === 'function') return '[Function]';
                                        if (typeof value === 'undefined') return '[undefined]';
                                        return value;
                                    });
                                } catch (e) {
                                    try {
                                        return Object.prototype.toString.call(arg);
                                    } catch {
                                        return '[Unserializable]';
                                    }
                                }
                            }
                            
                            function sendMessage(level, args) {
                                if (args.length === 0) return;
                                const message = args.map(serializeArg).join(' ');
                                const trimmed = message.trim();
                                
                                // Filter out trace messages, undefined, and empty messages
                                if (!trimmed || 
                                    trimmed === 'undefined' || 
                                    trimmed.includes('Trace:') ||
                                    trimmed.includes('Debug:')) {
                                    return;
                                }
                                
                                window.chrome.webview.postMessage(level + ': ' + message);
                            }
                            
                            console.log = function(...args) {
                                sendMessage('LOG', args);
                                originalLog.apply(console, args);
                            };
                            console.error = function(...args) {
                                sendMessage('ERROR', args);
                                originalError.apply(console, args);
                            };
                            console.warn = function(...args) {
                                sendMessage('WARN', args);
                                originalWarn.apply(console, args);
                            };
                            console.info = function(...args) {
                                sendMessage('INFO', args);
                                originalInfo.apply(console, args);
                            };
                            console.debug = function(...args) {
                                // Don't send debug messages at all
                                originalDebug.apply(console, args);
                            };
                        })();
                    ");
                    
                    log.Info("Console message logging enabled");
                }

                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                log.Info($"Navigating to {url}");
                WebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                log.Error($"Exception: {ex.Message}\n{ex.StackTrace}");
                System.Windows.MessageBox.Show(ex.Message);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var targetUrl = e.Uri;
            log.Debug($"Navigation starting to {targetUrl}, exitUrl: {exitUrl}, allowExit: {allowExit}");
            UpdateExitPopup(targetUrl);
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var currentUrl = WebView.CoreWebView2.Source.ToString();
            log.Debug($"Navigation completed fired, currentUrl: {currentUrl}, exitUrl: {exitUrl}, allowExit: {allowExit}");
            UpdateExitPopup(currentUrl);
        }

        private void WebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            var currentUrl = WebView.Source?.ToString() ?? string.Empty;
            log.Debug($"Source changed to {currentUrl}");
            UpdateExitPopup(currentUrl);
        }

        private void UpdateExitPopup(string? currentUrl)
        {
            var url = currentUrl ?? string.Empty;
            bool show = allowExit && UrlMatchesExit(url, exitUrl);
            ExitPopup.IsOpen = show;
        }

        private static bool UrlMatchesExit(string current, string exit)
        {
            if (string.IsNullOrWhiteSpace(exit))
                return false;

            try
            {
                var currentUri = new Uri(current, UriKind.Absolute);
                var exitUri = new Uri(exit, UriKind.Absolute);

                // Match host and absolute path without query/fragment; ignore trailing slash differences.
                var currentPath = currentUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                var exitPath = exitUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

                return string.Equals(currentUri.Host, exitUri.Host, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(currentPath, exitPath, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(currentUri.Scheme, exitUri.Scheme, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = e.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(message) && message.Length > 6)
                {
                    log.Debug($"[CONSOLE] {message}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error logging console message: {ex.Message}");
            }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
