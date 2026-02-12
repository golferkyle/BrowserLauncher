using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using log4net;
using log4net.Config;

namespace BrowserHost
{
    public partial class MainWindow : Window
    {
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static readonly ILog log = LogManager.GetLogger(typeof(MainWindow));
        private bool allowExit;
        private string exitUrl = string.Empty;
        private readonly int monitorIndex = -1;
        private bool logConsoleMessages = false;
        private string localStorageJson = string.Empty;
        private bool devTools = false;
        // Tracks whether a navigation has started recently to detect if Reload() took effect
        private volatile bool navigationStartedRecently = false;
        private string initialUrl = string.Empty;
        private DateTime lastPullRequestUtc = DateTime.MinValue;
        private volatile bool confirmDialogOpen = false;
        private DateTime lastKeyboardLaunchUtc = DateTime.MinValue;

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
                initialUrl = url;
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
                try
                {
                    var availableVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
                    log.Info($"Available WebView2 browser version: {availableVersion}");
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not determine installed WebView2 browser version: {ex.Message}");
                }

                // Initialize WebView2 normally (we'll implement a JS-based pull-to-refresh)
                await WebView.EnsureCoreWebView2Async();
                log.Info("WebView2 ensured");

                WebView.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
                WebView.SourceChanged += WebView_SourceChanged;

                // Always listen for web messages so injected pull-to-refresh can request a reload
                WebView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

                // Log runtime/browser version to help diagnose gesture support
                try
                {
                    var ver = WebView.CoreWebView2.Environment.BrowserVersionString;
                    log.Info($"WebView2 runtime version: {ver}");
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not read WebView2 runtime version: {ex.Message}");
                }

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

                                var escapedValue = value.Replace("\\", "\\\\").Replace("'", "\\'")
                                                        .Replace("\n", "\\n").Replace("\r", "\\r");
                                scriptLines.AppendLine($"localStorage.setItem('{kvp.Key}', '{escapedValue}');");
                            }

                            var script = scriptLines.ToString();
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

                // Inject JS-based pull-to-refresh (custom implementation)
                try
                {
                    await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                        (function() {
                            if (window.__customPullToRefreshInstalled) return;
                            window.__customPullToRefreshInstalled = true;

                            const threshold = 80; // px to trigger refresh
                            let startY = 0;
                            let pulling = false;
                            let lastTranslate = 0;
                            let container = null;

                            function createContainer() {
                                container = document.createElement('div');
                                container.style.position = 'fixed';
                                container.style.top = '0';
                                container.style.left = '0';
                                container.style.right = '0';
                                container.style.height = '0px';
                                container.style.display = 'flex';
                                container.style.alignItems = 'center';
                                container.style.justifyContent = 'center';
                                container.style.zIndex = '2147483647';
                                container.style.transform = 'translateY(-100%)';
                                container.style.transition = 'transform 200ms ease';

                                const spinner = document.createElement('div');
                                spinner.style.width = '36px';
                                spinner.style.height = '36px';
                                spinner.style.border = '4px solid rgba(0,0,0,0.15)';
                                spinner.style.borderTop = '4px solid rgba(0,0,0,0.6)';
                                spinner.style.borderRadius = '50%';
                                spinner.style.boxSizing = 'border-box';
                                spinner.style.animation = 'cptr-spin 1s linear infinite';

                                const style = document.createElement('style');
                                style.textContent = '@keyframes cptr-spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }';
                                document.head.appendChild(style);

                                container.appendChild(spinner);
                                document.documentElement.prepend(container);
                            }

                            function removeContainer() {
                                try {
                                    if (container) {
                                        container.style.transition = 'transform 200ms ease';
                                        container.style.transform = 'translateY(-100%)';
                                        setTimeout(() => { try { container.remove(); } catch{}; container = null; }, 250);
                                    }
                                } catch(e){}
                            }

                            window.addEventListener('touchstart', function(e) {
                                try {
                                    if (document.scrollingElement && document.scrollingElement.scrollTop > 0) return;
                                    startY = e.touches[0].clientY;
                                    pulling = true;
                                    lastTranslate = 0;
                                } catch(e) {}
                            }, {passive: true});

                            window.addEventListener('touchmove', function(e) {
                                try {
                                    if (!pulling) return;
                                    const dy = e.touches[0].clientY - startY;
                                    if (dy > 0 && (document.scrollingElement ? document.scrollingElement.scrollTop === 0 : window.scrollY === 0)) {
                                        e.preventDefault();
                                        if (!container) createContainer();
                                        const translate = Math.min(dy / 2, 150);
                                        container.style.transform = `translateY(${translate}px)`;
                                        lastTranslate = translate;
                                    }
                                } catch(e) {}
                            }, {passive: false});

                            window.addEventListener('touchend', function(e) {
                                try {
                                    if (!pulling) return;
                                    pulling = false;
                                    if (lastTranslate > threshold) {
                                        // show spinner in visible area and refresh
                                        if (container) {
                                            container.style.transition = 'transform 150ms ease';
                                            container.style.transform = 'translateY(60px)';
                                        }
                                        setTimeout(function() {
                                            try { window.chrome.webview.postMessage('PULL_TO_REFRESH'); } catch(e) { removeContainer(); }
                                        }, 150);
                                    } else {
                                        removeContainer();
                                    }
                                    lastTranslate = 0;
                                } catch(e) {}
                            }, {passive: true});
                        })();
                    ");
                    log.Info("Injected custom pull-to-refresh script");
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to inject custom pull-to-refresh script: {ex.Message}");
                }

                // Inject touch-aware editable-focus detection to trigger on-screen keyboard
                try
                {
                    await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                        (function() {
                            if (window.__touchKeyboardBridgeInstalled) return;
                            window.__touchKeyboardBridgeInstalled = true;

                            let lastPostedTs = 0;

                            function getDeepActiveElement(rootDoc) {
                                let el = rootDoc && rootDoc.activeElement ? rootDoc.activeElement : null;
                                while (el && el.shadowRoot && el.shadowRoot.activeElement) {
                                    el = el.shadowRoot.activeElement;
                                }
                                return el;
                            }

                            function isEditable(target) {
                                if (!target || target.disabled || target.readOnly) return false;

                                const tag = (target.tagName || '').toLowerCase();
                                if (tag === 'textarea') return true;
                                if (target.isContentEditable) return true;
                                const role = (target.getAttribute && target.getAttribute('role')) || '';
                                if (role.toLowerCase() === 'textbox') return true;

                                if (tag === 'input') {
                                    const type = (target.type || 'text').toLowerCase();
                                    return type === 'text' ||
                                           type === 'search' ||
                                           type === 'url' ||
                                           type === 'tel' ||
                                           type === 'email' ||
                                           type === 'password' ||
                                           type === 'number';
                                }

                                return false;
                            }

                            function maybePostShowKeyboard(target) {
                                if (!isEditable(target)) return;

                                const now = Date.now();
                                // debounce duplicate posts
                                if ((now - lastPostedTs) < 400) return;

                                lastPostedTs = now;
                                try { window.chrome.webview.postMessage('SHOW_OSK'); } catch (e) {}
                            }

                            // Fire early when user touches editable controls
                            window.addEventListener('pointerdown', function(e) {
                                if (!e) return;
                                if (e.pointerType === 'touch') {
                                    maybePostShowKeyboard(e.target);
                                }
                            }, true);

                            window.addEventListener('touchstart', function(e) {
                                try {
                                    maybePostShowKeyboard(e.target);
                                } catch (err) {}
                            }, true);
                        })();
                    ");
                    log.Info("Injected touch keyboard bridge script");
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to inject touch keyboard bridge script: {ex.Message}");
                }

                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                // Enable swipe navigation and pull-to-refresh gestures if supported by the WebView2 runtime
                try
                {
                    var settings = WebView.CoreWebView2.Settings;
                    var swipeProp = settings.GetType().GetProperty("IsSwipeNavigationEnabled");
                    if (swipeProp != null)
                    {
                        log.Info("IsSwipeNavigationEnabled property found on settings");
                        if (swipeProp.CanWrite)
                        {
                            swipeProp.SetValue(settings, true);
                            log.Info($"IsSwipeNavigationEnabled set to {swipeProp.GetValue(settings)}");
                        }
                        else
                        {
                            log.Warn("IsSwipeNavigationEnabled property is read-only");
                        }
                    }
                    else
                    {
                        log.Info("IsSwipeNavigationEnabled property not present on runtime settings");
                    }

                    log.Info("Attempted to enable swipe navigation (native pull-to-refresh removed)");
                }
                catch (Exception ex)
                {
                    log.Warn($"Could not enable swipe/pull-to-refresh settings: {ex.Message}");
                }

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
            // mark that a navigation has started so reload detection can use this
            try { navigationStartedRecently = true; }
            catch { }
            // clear the flag after a short window
            _ = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(2000); navigationStartedRecently = false; });
            UpdateExitPopup(targetUrl);
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var currentUrl = WebView.CoreWebView2.Source.ToString();
            UpdateExitPopup(currentUrl);
        }

        private void WebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            var currentUrl = WebView.Source?.ToString() ?? string.Empty;
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
                string message = null;
                try
                {
                    message = e.TryGetWebMessageAsString();
                }
                catch (Exception getEx)
                {
                    log.Warn($"Failed to get web message as string: {getEx.Message}");
                }

                if (string.Equals(message, "PULL_TO_REFRESH", StringComparison.Ordinal))
                {
                    var now = DateTime.UtcNow;
                    // Debounce repeated requests coming from residual touch events
                    if ((now - lastPullRequestUtc) < TimeSpan.FromMilliseconds(800))
                    {
                        log.Info("Ignoring duplicate pull-to-refresh request (debounced)");
                        return;
                    }
                    lastPullRequestUtc = now;
                    log.Info("Pull-to-refresh requested from page");
                    if (confirmDialogOpen)
                    {
                        log.Info("Confirmation dialog already open — ignoring pull-to-refresh request");
                        return;
                    }
                    try
                    {
                        // Show our custom modal confirmation window with labeled buttons
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                confirmDialogOpen = true;
                                using (var dlg = new RefreshConfirmWindow() { Owner = this })
                                {
                                    dlg.ShowDialog();
                                    var choice = dlg.Choice;
                                    // reset the last pull time to avoid immediate duplicates
                                    lastPullRequestUtc = DateTime.UtcNow;

                                    if (choice == RefreshChoice.Refresh)
                                    {
                                        log.Info("User chose: Refresh");
                                        PerformReload();
                                    }
                                    else if (choice == RefreshChoice.Cancel)
                                    {
                                        log.Info("User chose: Cancel (do nothing)");
                                    }
                                    else if (choice == RefreshChoice.Home)
                                    {
                                        log.Info("User chose: Home");
                                        try
                                        {
                                            if (!string.IsNullOrWhiteSpace(initialUrl))
                                            {
                                                WebView.CoreWebView2.Navigate(initialUrl);
                                                log.Info($"Navigated home to {initialUrl} after user choice");
                                            }
                                        }
                                        catch (Exception nx)
                                        {
                                            log.Error($"Failed to navigate home after user choice: {nx.Message}");
                                        }
                                    }
                                }
                            }
                            catch (Exception ux)
                            {
                                log.Error($"Failed to show refresh confirmation dialog: {ux.Message}");
                            }
                            finally
                            {
                                confirmDialogOpen = false;
                            }
                        });
                    }
                    catch (Exception dx)
                    {
                        log.Error($"Error showing confirmation dialog: {dx.Message}");
                    }
                    return;
                }

                if (string.Equals(message, "SHOW_OSK", StringComparison.Ordinal))
                {
                    log.Info("SHOW_OSK requested from web content");
                    ShowOnScreenKeyboard();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(message) && message.Length > 6)
                {
                    log.Info($"[CONSOLE] {message}");
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

        private void PerformReload()
        {
            try
            {
                var current = WebView?.CoreWebView2?.Source ?? string.Empty;
                log.Info($"PerformReload invoked for: {current}");

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (WebView?.CoreWebView2 == null)
                        {
                            log.Error("Cannot reload: CoreWebView2 is null");
                            return;
                        }
                        WebView.CoreWebView2.Reload();
                        log.Info("Called CoreWebView2.Reload() from PerformReload");
                    }
                    catch (Exception rx)
                    {
                        log.Error($"Reload failed: {rx.Message}");
                    }
                });

                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(700);
                    if (!navigationStartedRecently)
                    {
                        try
                        {
                            string src = null;
                            try
                            {
                                src = Dispatcher.Invoke(() => WebView?.CoreWebView2?.Source);
                            }
                            catch (Exception readEx)
                            {
                                log.Warn($"Could not read CoreWebView2.Source from UI thread: {readEx.Message}");
                            }

                            if (!string.IsNullOrWhiteSpace(src))
                            {
                                log.Warn("Reload did not trigger navigation; performing forced navigate (about:blank -> original)");
                                Dispatcher.Invoke(() =>
                                {
                                    try
                                    {
                                        WebView.CoreWebView2.Navigate("about:blank");
                                    }
                                    catch (Exception nx)
                                    {
                                        log.Error($"Failed to navigate to about:blank: {nx.Message}");
                                    }
                                });
                                await System.Threading.Tasks.Task.Delay(250);
                                var cacheBusted = src + (src.Contains("?") ? "&" : "?") + "_r=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                Dispatcher.Invoke(() =>
                                {
                                    try
                                    {
                                        WebView.CoreWebView2.Navigate(cacheBusted);
                                        log.Info($"Performed forced navigate to reload (cache-busted): {cacheBusted}");
                                    }
                                    catch (Exception nx)
                                    {
                                        log.Error($"Forced navigate failed: {nx.Message}");
                                    }
                                });
                            }
                        }
                        catch (Exception exf)
                        {
                            log.Error($"Error during forced reload logic: {exf.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                log.Error($"PerformReload error: {ex.Message}");
            }
        }

        private void ShowOnScreenKeyboard()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - lastKeyboardLaunchUtc) < TimeSpan.FromMilliseconds(700))
                {
                    return;
                }
                lastKeyboardLaunchUtc = now;

                // BrowserHost is configured Topmost; lower it so OSK windows can appear above it.
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (Topmost)
                        {
                            Topmost = false;
                            log.Info("Temporarily disabled Topmost to allow keyboard visibility");
                        }
                    });
                }
                catch { }

                var tabTipPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    "microsoft shared",
                    "ink",
                    "TabTip.exe");

                if (!File.Exists(tabTipPath))
                {
                    tabTipPath = @"C:\Program Files\Common Files\Microsoft Shared\ink\TabTip.exe";
                }

                var started = false;
                if (File.Exists(tabTipPath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = tabTipPath,
                            UseShellExecute = true
                        });
                        started = true;
                        log.Info("Launched touch keyboard (TabTip.exe)");
                    }
                    catch (Exception tabTipEx)
                    {
                        log.Warn($"Failed to launch TabTip.exe: {tabTipEx.Message}");
                    }
                }

                // Fallback only if TabTip is unavailable
                if (!started)
                {
                    try
                    {
                        EnsureOskVisible(false);
                    }
                    catch (Exception oskEx)
                    {
                        log.Warn($"Failed to launch osk.exe fallback: {oskEx.Message}");
                    }
                }
                else
                {
                    // TabTip can start without showing UI on some systems; enforce OSK fallback unless already running.
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        try
                        {
                            var oskRunning = Process.GetProcessesByName("osk").Length > 0;
                            if (!oskRunning)
                            {
                                EnsureOskVisible(false);
                                log.Info("Launched enforced fallback keyboard path after TabTip");
                            }
                            else
                            {
                                EnsureOskVisible(true);
                                log.Info("OSK already running; forced relaunch path executed");
                            }
                        }
                        catch (Exception delayedFallbackEx)
                        {
                            log.Warn($"Delayed fallback to osk.exe failed: {delayedFallbackEx.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to launch on-screen keyboard: {ex.Message}");
            }
        }

        private bool TryRestoreOskWindow()
        {
            try
            {
                var oskWindow = FindWindow("OSKMainClass", null);
                if (oskWindow == IntPtr.Zero)
                {
                    return false;
                }

                try { ShowWindow(oskWindow, SW_RESTORE); } catch { }
                try { ShowWindow(oskWindow, SW_SHOW); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool EnsureOskVisible(bool forceRelaunchIfRunning)
        {
            try
            {
                if (!forceRelaunchIfRunning && TryRestoreOskWindow())
                {
                    log.Info("Restored existing OSK window");
                    return true;
                }

                var oskProcesses = Process.GetProcessesByName("osk");
                if (oskProcesses.Length > 0)
                {
                    foreach (var process in oskProcesses)
                    {
                        try
                        {
                            process.Kill(true);
                            process.WaitForExit(1000);
                        }
                        catch { }
                    }
                    log.Info("Terminated stale OSK process instances before relaunch");
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "osk.exe",
                    UseShellExecute = true
                });
                log.Info("Launched fallback keyboard (osk.exe)");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"EnsureOskVisible failed: {ex.Message}");
                return false;
            }
        }
    }
}
