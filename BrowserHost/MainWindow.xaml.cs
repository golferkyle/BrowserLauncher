using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using log4net;
using log4net.Config;
using BrowserHost.Services;

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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private static readonly ILog log = LogManager.GetLogger(typeof(MainWindow));
        private bool allowExit;
        private string exitUrl = string.Empty;
        private readonly int monitorIndex = -1;
        private bool logConsoleMessages = false;
        private string localStorageJson = string.Empty;
        private bool devTools = false;
        private bool enableOnScreenKeyboard = true;
        // Tracks whether a navigation has started recently to detect if Reload() took effect
        private volatile bool navigationStartedRecently = false;
        private string initialUrl = string.Empty;
        private DateTime lastPullRequestUtc = DateTime.MinValue;
        private volatile bool confirmDialogOpen = false;

        // Touch keyboard settings (populated from per-screen config JSON written by Launcher)
        private bool _enableOskFallback = false;       // allow osk.exe drop if touch keyboard unavailable
        private int _keyboardAnimationMs = 200;        // animation duration for WebView margin adjustment (from uisettings.json)
        private int _keyboardPollIntervalMs = 150;     // keyboard rect poll interval ms (from uisettings.json)
        private bool _kioskTopmost = true;             // desired Topmost state when keyboard is not open

        // Bottom bar settings
        private bool _autoHideBottomBar = false;       // true = bar slides in/out on demand
        private bool _bottomBarEnabled = true;         // per-screen kill switch
        private bool _barAnimating = false;            // guard against overlapping animations
        private int _bottomBarTimeoutSec = 5;          // auto-hide delay (from uisettings.json)

        // Touch keyboard runtime state
        private readonly TouchKeyboardService _touchKeyboard = new();
        private DispatcherTimer? _keyboardPollTimer;
        private Rect? _lastKeyboardRectForAnim;
        private int _stableTickCount;

        // Bottom bar runtime state
        private DispatcherTimer? _autoHideTimer;
        private bool _onExitUrl = false;             // tracks whether current URL matches exitUrl

        // Exit settings
        private bool _alwaysAllowExit = false;         // show Exit option in pull-down refresh menu
        private string _exitConfirmPrompt = "Are you sure you want to Exit?"; // configurable prompt
        private int _zoom = 100;                       // per-screen WebView2 zoom level (100 = 100%)

        // Mouse pull-tab state
        private PullTabWindow? _pullTabWindow;          // overlay window (separate HWND, renders above WebView2)
        private bool _pullTabVisible = false;           // true while tab is slid into view
        private DispatcherTimer? _pullTabHideTimer;     // auto-retract after idle
        private DispatcherTimer? _mouseHotZoneTimer;    // polls GetCursorPos for hot-zone detection

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
                if (args.Length < 3 || monitorIndex < 0)
                {
                    log.Info("Expected args: <MonitorIndex> <ConfigFilePath>. Shutting down.");
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                // Read all configuration from the JSON config file written by the Launcher.
                // This avoids fragile command-line escaping of complex JSON values.
                var configFilePath = args[2];
                log.Info($"Reading config from: {configFilePath}");
                var configJson = File.ReadAllText(configFilePath);
                var configDoc = System.Text.Json.JsonDocument.Parse(configJson);
                var root = configDoc.RootElement;

                string url = root.GetProperty("Url").GetString() ?? string.Empty;
                initialUrl = url;
                allowExit = root.TryGetProperty("AllowExit", out var ae) && ae.GetBoolean();
                exitUrl = root.TryGetProperty("ExitUrl", out var eu) ? eu.GetString() ?? string.Empty : string.Empty;
                logConsoleMessages = root.TryGetProperty("LogConsoleMessages", out var lc) && lc.GetBoolean();
                devTools = root.TryGetProperty("DevTools", out var dt) && dt.GetBoolean();
                enableOnScreenKeyboard = !root.TryGetProperty("EnableOnScreenKeyboard", out var eok) || eok.GetBoolean();

                _enableOskFallback = root.TryGetProperty("EnableOskFallback", out var eof) && eof.GetBoolean();
                _autoHideBottomBar = root.TryGetProperty("AutoHideBottomBar", out var ahb) && ahb.GetBoolean();
                _bottomBarEnabled = !root.TryGetProperty("BottomBarEnabled", out var bbe) || bbe.GetBoolean();
                _alwaysAllowExit = root.TryGetProperty("AlwaysAllowExit", out var aae) && aae.GetBoolean();
                _zoom = root.TryGetProperty("Zoom", out var zf) ? zf.GetInt32() : 100;

                // Load animation/poll intervals from uisettings.json (BrowserHost dir), not from Launcher config
                LoadUiSettings();

                if (root.TryGetProperty("LocalStorage", out var localStorageElement))
                {
                    var localStorageText = localStorageElement.GetRawText();
                    if (localStorageText != "{}")
                    {
                        localStorageJson = localStorageText;
                    }
                }

                // If AllowExit is true and ExitUrl is empty, default to initial URL
                // If AllowExit is false and ExitUrl is empty, keep it empty (button never shows)
                if (string.IsNullOrWhiteSpace(exitUrl) && allowExit)
                {
                    exitUrl = url;
                    log.Info($"ExitUrl not provided; using initial URL as exit target: {exitUrl}");
                }
                log.Info($"Monitor: {monitorIndex}, URL: {url}, AllowExit: {allowExit}, ExitUrl: {exitUrl}, LogConsoleMessages: {logConsoleMessages}, DevTools: {devTools}, LocalStorage: {(string.IsNullOrWhiteSpace(localStorageJson) ? "none" : "configured")}, Zoom: {_zoom}, EnableOskFallback: {_enableOskFallback}, AutoHideBottomBar: {_autoHideBottomBar}, BottomBarEnabled: {_bottomBarEnabled}, AlwaysAllowExit: {_alwaysAllowExit}, KeyboardAnimationMs: {_keyboardAnimationMs}, KeyboardPollIntervalMs: {_keyboardPollIntervalMs}, BottomBarTimeoutSec: {_bottomBarTimeoutSec}");

                // Show keyboard button only in Button mode
                KeyboardButton.Visibility = enableOnScreenKeyboard ? Visibility.Visible : Visibility.Collapsed;

                // Configure bottom bar initial state
                if (!_bottomBarEnabled)
                {
                    BottomBar.Visibility = Visibility.Collapsed;
                    log.Info("BottomBar disabled for this screen");
                }
                else if (_autoHideBottomBar)
                {
                    BottomBar.Visibility = Visibility.Collapsed;
                    log.Info("BottomBar in auto-hide mode, starts hidden");
                }
                else
                {
                    ShowBottomBar(animate: false);
                    log.Info("BottomBar always visible");
                }

                // Read the resolved screen bounds that Launcher computed using
                // position-based (left-to-right) monitor mapping.
                if (root.TryGetProperty("ResolvedLeft", out var rl) &&
                    root.TryGetProperty("ResolvedTop", out var rt) &&
                    root.TryGetProperty("ResolvedWidth", out var rw) &&
                    root.TryGetProperty("ResolvedHeight", out var rh))
                {
                    Left = rl.GetInt32();
                    Top = rt.GetInt32();
                    Width = rw.GetInt32();
                    Height = rh.GetInt32();

                    var resolvedDevice = root.TryGetProperty("ResolvedDeviceName", out var rd) ? rd.GetString() : "unknown";
                    log.Info($"Using Launcher-resolved bounds: Left={Left}, Top={Top}, Width={Width}, Height={Height}, Device={resolvedDevice}");
                }
                else
                {
                    // Fallback: Launcher did not provide resolved bounds (e.g. older Launcher version).
                    // Use position-based detection directly.
                    var screens = Screen.AllScreens
                        .OrderBy(s => s.Bounds.X)
                        .ThenBy(s => s.Bounds.Y)
                        .ToArray();
                    log.Info($"No resolved bounds in config — falling back to local detection. Available screens: {screens.Length}");
                    if (monitorIndex >= screens.Length)
                    {
                        log.Error($"Monitor index {monitorIndex} out of range (only {screens.Length} monitor(s) detected)");
                        System.Windows.MessageBox.Show(
                            $"Unable to launch application on monitor {monitorIndex}.\n\nOnly {screens.Length} monitor(s) detected. Monitor index {monitorIndex} is not available.",
                            "BrowserHost - Monitor Not Found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        System.Windows.Application.Current.Shutdown(2);
                        return;
                    }

                    var screen = screens[monitorIndex];
                    Left = screen.Bounds.Left;
                    Top = screen.Bounds.Top;
                    Width = screen.Bounds.Width;
                    Height = screen.Bounds.Height;
                    log.Info($"Fallback screen bounds: {screen.DeviceName} at {screen.Bounds}");
                }

                // If Launcher detected missing monitors and this screen is launching anyway,
                // show a brief warning overlay about which screens could not start.
                if (root.TryGetProperty("SkippedMonitors", out var skippedEl) &&
                    root.TryGetProperty("ExpectedMonitorCount", out var expEl) &&
                    root.TryGetProperty("DetectedMonitorCount", out var detEl))
                {
                    var expected = expEl.GetInt32();
                    var detected = detEl.GetInt32();
                    var skippedList = new List<int>();
                    foreach (var item in skippedEl.EnumerateArray())
                        skippedList.Add(item.GetInt32());

                    var skippedStr = string.Join(", ", skippedList);
                    var warningMsg = $"Warning: Only {detected} of {expected} expected monitor(s) detected.\n" +
                                     $"Monitor(s) {skippedStr} could not be launched.";
                    log.Warn(warningMsg);
                    System.Windows.MessageBox.Show(
                        warningMsg,
                        "BrowserHost - Missing Monitors",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

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

                // Initialize WebView2 with a per-monitor user data folder so each instance
                // has its own isolated session (localStorage, cookies, cache, etc.)
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrowserHost",
                    $"Monitor{monitorIndex}");
                log.Info($"WebView2 user data folder: {userDataFolder}");
                var webView2Env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebView.EnsureCoreWebView2Async(webView2Env);
                log.Info("WebView2 ensured with per-monitor profile");

                if (_zoom != 100)
                {
                    WebView.ZoomFactor = _zoom / 100.0;
                    log.Info($"Zoom set to {_zoom}% ({_zoom / 100.0})");
                }

                WebView.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
                WebView.SourceChanged += WebView_SourceChanged;
                WebView.GotFocus += WebView_GotFocus;

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
                                // Use Serialize instead of GetRawText to produce compact JSON
                                // matching what JavaScript's JSON.stringify() would output.
                                // GetRawText preserves source formatting (whitespace, newlines)
                                // which can break web apps that compare localStorage values.
                                var value = System.Text.Json.JsonSerializer.Serialize(kvp.Value);

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

                // SHOW_OSK JS bridge removed — keyboard is always Button mode.
                if (!enableOnScreenKeyboard)
                {
                    log.Info("SHOW_OSK JS bridge skipped (EnableOnScreenKeyboard=false)");
                }
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

                // Inject text-input focus detection to show/reset auto-hide bottom bar
                if (_autoHideBottomBar && _bottomBarEnabled)
                {
                    try
                    {
                        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                            (function() {
                                if (window.__textInputBridgeInstalled) return;
                                window.__textInputBridgeInstalled = true;

                                function isEditable(el) {
                                    if (!el || el.disabled || el.readOnly) return false;
                                    var tag = (el.tagName || '').toLowerCase();
                                    if (tag === 'textarea') return true;
                                    if (el.isContentEditable) return true;
                                    var role = (el.getAttribute && el.getAttribute('role')) || '';
                                    if (role.toLowerCase() === 'textbox') return true;
                                    if (tag === 'input') {
                                        var type = (el.type || 'text').toLowerCase();
                                        return type === 'text' || type === 'search' || type === 'url' ||
                                               type === 'tel' || type === 'email' || type === 'password' || type === 'number';
                                    }
                                    return false;
                                }

                                document.addEventListener('focusin', function(e) {
                                    try {
                                        if (isEditable(e.target))
                                            window.chrome.webview.postMessage('TEXTINPUT_FOCUSED');
                                    } catch(err) {}
                                }, true);

                                document.addEventListener('focusout', function(e) {
                                    try {
                                        setTimeout(function() {
                                            if (!isEditable(document.activeElement))
                                                window.chrome.webview.postMessage('TEXTINPUT_BLURRED');
                                        }, 100);
                                    } catch(err) {}
                                }, true);
                            })();
                        ");
                        log.Info("Injected text input focus bridge for auto-hide bottom bar");
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"Failed to inject text input bridge: {ex.Message}");
                    }
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
                
                // Update exit button visibility before navigation so it shows immediately
                UpdateExitButton(url);
                
                WebView.CoreWebView2.Navigate(url);

                // Initialize the pull-tab overlay window (separate HWND above WebView2)
                InitPullTabOverlay();
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
            UpdateExitButton(targetUrl);
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var currentUrl = WebView.CoreWebView2.Source.ToString();
            UpdateExitButton(currentUrl);
        }

        private void WebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            var currentUrl = WebView.Source?.ToString() ?? string.Empty;
            UpdateExitButton(currentUrl);
        }

        private void WebView_GotFocus(object sender, RoutedEventArgs e)
        {
            // Reassert exit button visibility when WebView2 focus changes.
            var currentUrl = WebView?.CoreWebView2?.Source ?? WebView.Source?.ToString() ?? string.Empty;
            UpdateExitButton(currentUrl);
        }

        private void UpdateExitButton(string? currentUrl)
        {
            var url = currentUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                ExitButton.Visibility = Visibility.Collapsed;
                return;
            }

            bool matches = UrlMatchesExit(url, exitUrl);
            bool wasOnExitUrl = _onExitUrl;
            _onExitUrl = matches;
            ExitButton.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;

            if (_autoHideBottomBar && _bottomBarEnabled)
            {
                if (matches)
                {
                    // Exit URL active: show bar and keep it permanently (stop auto-hide)
                    ShowBottomBar();
                    _autoHideTimer?.Stop();
                }
                else if (wasOnExitUrl)
                {
                    // Just left exit URL: start auto-hide countdown so the bar fades out
                    ResetAutoHideTimer();
                }
                // Normal navigation (non-exit → non-exit): do not reset timer;
                // the countdown set by TEXTINPUT_FOCUSED/BLURRED should run uninterrupted.
            }
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
                    ShowRefreshDialog();
                    return;
                }

                if (string.Equals(message, "TEXTINPUT_FOCUSED", StringComparison.Ordinal))
                {
                    if (_autoHideBottomBar && _bottomBarEnabled)
                    {
                        ShowBottomBar();
                        ResetAutoHideTimer();
                    }
                    return;
                }

                if (string.Equals(message, "TEXTINPUT_BLURRED", StringComparison.Ordinal))
                {
                    if (_autoHideBottomBar && _bottomBarEnabled)
                        ResetAutoHideTimer();
                    return;
                }

                if (string.Equals(message, "SHOW_OSK", StringComparison.Ordinal))
                {
                    // SHOW_OSK is no longer honoured — keyboard is always Button mode.
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

        private void ShowBottomBar(bool animate = true)
        {
            if (!_bottomBarEnabled) return;
            if (BottomBar.Visibility == Visibility.Visible && BottomBarTranslate.Y < 1) return;
            if (_barAnimating) return;

            // Clear any held animation and set base value to off-screen start position
            BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            BottomBarTranslate.Y = BottomBar.Height;
            BottomBar.Visibility = Visibility.Visible;

            if (!animate)
            {
                BottomBarTranslate.Y = 0;
                return;
            }

            _barAnimating = true;
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (s, e) =>
            {
                _barAnimating = false;
                BottomBarTranslate.Y = 0;
                BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            };
            BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void HideBottomBar()
        {
            if (!_bottomBarEnabled || BottomBar.Visibility != Visibility.Visible) return;
            if (_barAnimating) return;

            BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            BottomBarTranslate.Y = 0;

            _barAnimating = true;
            var anim = new DoubleAnimation(BottomBar.Height, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (s, e) =>
            {
                _barAnimating = false;
                BottomBarTranslate.Y = BottomBar.Height;
                BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                BottomBar.Visibility = Visibility.Collapsed;
            };
            BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void ResetAutoHideTimer()
        {
            if (!_autoHideBottomBar || !_bottomBarEnabled) return;
            if (_autoHideTimer == null)
            {
                _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_bottomBarTimeoutSec) };
                _autoHideTimer.Tick += AutoHideTimer_Tick;
            }
            else
            {
                // Update interval in case uisettings were reloaded
                _autoHideTimer.Interval = TimeSpan.FromSeconds(_bottomBarTimeoutSec);
            }
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            _autoHideTimer!.Stop();
            // Keep bar visible while exit button is shown
            if (ExitButton.Visibility == Visibility.Visible)
            {
                _autoHideTimer.Start();
                return;
            }
            log.Info("Auto-hiding bottom bar after timeout");
            HideBottomBar();
        }

        private void LoadUiSettings()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "uisettings.json");
                if (!File.Exists(path)) return;
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("KeyboardAnimationMs", out var kams)) _keyboardAnimationMs = kams.GetInt32();
                if (root.TryGetProperty("KeyboardPollIntervalMs", out var kpims)) _keyboardPollIntervalMs = kpims.GetInt32();
                if (root.TryGetProperty("BottomBarTimeoutSec", out var bbts)) _bottomBarTimeoutSec = bbts.GetInt32();
                if (root.TryGetProperty("ExitConfirmPrompt", out var ecp)) _exitConfirmPrompt = ecp.GetString() ?? _exitConfirmPrompt;
                log.Info($"uisettings.json loaded: KeyboardAnimationMs={_keyboardAnimationMs}, KeyboardPollIntervalMs={_keyboardPollIntervalMs}, BottomBarTimeoutSec={_bottomBarTimeoutSec}");
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to load uisettings.json: {ex.Message}");
            }
        }

        private void KeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            // Lower Topmost so the keyboard window can render above this window.
            Topmost = false;
            var hwnd = new WindowInteropHelper(this).Handle;

            if (!_touchKeyboard.Toggle(hwnd) && _enableOskFallback)
            {
                log.Warn("Touch keyboard unavailable; using accessibility OSK fallback");
                ShowAccessibilityOsk();
            }

            StartKeyboardPollTimer();
        }

        private void StartKeyboardPollTimer()
        {
            _stableTickCount = 0;
            _lastKeyboardRectForAnim = null;

            if (_keyboardPollTimer == null)
            {
                _keyboardPollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(_keyboardPollIntervalMs)
                };
                _keyboardPollTimer.Tick += KeyboardPollTimer_Tick;
            }

            _keyboardPollTimer.Start();
        }

        private void KeyboardPollTimer_Tick(object? sender, EventArgs e)
        {
            var kbRect = _touchKeyboard.GetKeyboardRect();

            if (kbRect == _lastKeyboardRectForAnim)
            {
                _stableTickCount++;
                if (_stableTickCount >= 5)
                {
                    _keyboardPollTimer!.Stop();
                    // Restore Topmost once keyboard is confirmed closed
                    if (!_touchKeyboard.IsOpen)
                    {
                        Topmost = _kioskTopmost;
                        log.Info("Keyboard closed; Topmost restored");
                    }
                    // Update button visual state
                    UpdateKeyboardButtonState(_touchKeyboard.IsOpen);
                }
                return;
            }

            _stableTickCount = 0;
            _lastKeyboardRectForAnim = kbRect;
            UpdateKeyboardButtonState(kbRect.HasValue);
            AnimateWebViewMargin(kbRect);
        }

        private void UpdateKeyboardButtonState(bool isOpen)
        {
            KeyboardButton.Background = isOpen
                ? new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))  // blue when open
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); // dark gray when closed
            KeyboardButtonText.Text = isOpen ? "Keyboard ▲" : "Keyboard";
        }

        private void AnimateWebViewMargin(Rect? keyboardRect)
        {
            var dpiSource = PresentationSource.FromVisual(this);
            var dpiY = dpiSource?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double newBottomMargin = 0;

            if (keyboardRect.HasValue)
            {
                // Window boundaries in screen pixels
                var windowBottomPx = (Top + ActualHeight) * dpiY;
                var barHeightPx = BottomBar.ActualHeight * dpiY;
                var webViewBottomPx = windowBottomPx - barHeightPx;

                var overlapPx = webViewBottomPx - keyboardRect.Value.Top;
                if (overlapPx > 0)
                    newBottomMargin = overlapPx / dpiY;
            }

            var currentMargin = WebViewContainer.Margin;
            if (Math.Abs(currentMargin.Bottom - newBottomMargin) < 1)
                return;

            var animation = new ThicknessAnimation
            {
                From = currentMargin,
                To = new Thickness(0, 0, 0, newBottomMargin),
                Duration = TimeSpan.FromMilliseconds(_keyboardAnimationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            WebViewContainer.BeginAnimation(Border.MarginProperty, animation);
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        // ── Mouse pull-tab ────────────────────────────────────────────────────────

        private void InitPullTabOverlay()
        {
            _pullTabWindow = new PullTabWindow { Owner = this };
            _pullTabWindow.MouseEnteredTab += () => _pullTabHideTimer?.Stop();
            _pullTabWindow.MouseLeftTab += () => StartPullTabHideTimer();
            _pullTabWindow.TabClicked += () => { RetractMousePullTab(); InvokePullToRefresh(); };

            // Start hidden above the main window
            PositionPullTab(visible: false);
            _pullTabWindow.Show();

            // Poll cursor via Win32 (WebView2 HwndHost swallows WPF mouse events)
            _mouseHotZoneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _mouseHotZoneTimer.Tick += (s, e) =>
            {
                if (!GetCursorPos(out POINT pt)) return;
                // Convert screen coords to main-window-relative coords
                var pos = PointFromScreen(new System.Windows.Point(pt.X, pt.Y));
                double tabW = 180;
                double centerLeft  = (ActualWidth - tabW) / 2;
                double centerRight = centerLeft + tabW;
                bool inHotZone = pos.Y >= 0 && pos.Y <= 10 && pos.X >= centerLeft && pos.X <= centerRight;
                if (inHotZone && !_pullTabVisible)
                    ShowMousePullTab();
            };
            _mouseHotZoneTimer.Start();
        }

        private void PositionPullTab(bool visible)
        {
            if (_pullTabWindow == null) return;
            // Center the 180-wide tab on the main window's top edge
            double tabW = 180, tabH = 40;
            _pullTabWindow.Left = Left + (ActualWidth - tabW) / 2;
            _pullTabWindow.Top  = visible ? Top : Top - tabH;
        }

        private void ShowMousePullTab()
        {
            _pullTabHideTimer?.Stop();
            if (_pullTabVisible || _pullTabWindow == null) return;
            _pullTabVisible = true;

            PositionPullTab(visible: false); // start hidden
            var from = Top - 40;
            var to = Top;
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (s, e) =>
            {
                _pullTabWindow.Top = to;
                StartPullTabHideTimer();
            };
            _pullTabWindow.BeginAnimation(Window.TopProperty, anim);
        }

        private void RetractMousePullTab()
        {
            _pullTabHideTimer?.Stop();
            if (!_pullTabVisible || _pullTabWindow == null) return;
            _pullTabVisible = false;

            var from = Top;
            var to = Top - 40;
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (s, e) =>
            {
                _pullTabWindow.BeginAnimation(Window.TopProperty, null);
                _pullTabWindow.Top = to;
            };
            _pullTabWindow.BeginAnimation(Window.TopProperty, anim);
        }

        private void StartPullTabHideTimer()
        {
            if (_pullTabHideTimer == null)
            {
                _pullTabHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _pullTabHideTimer.Tick += (s, e) => { _pullTabHideTimer.Stop(); RetractMousePullTab(); };
            }
            _pullTabHideTimer.Stop();
            _pullTabHideTimer.Start();
        }

        private void InvokePullToRefresh()
        {
            var now = DateTime.UtcNow;
            if ((now - lastPullRequestUtc) < TimeSpan.FromMilliseconds(800)) return;
            lastPullRequestUtc = now;
            log.Info("Pull-to-refresh requested by mouse pull-tab");
            ShowRefreshDialog();
        }

        private void ShowRefreshDialog()
        {
            if (confirmDialogOpen)
            {
                log.Info("Confirmation dialog already open — ignoring pull-to-refresh request");
                return;
            }
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        confirmDialogOpen = true;
                        using (var dlg = new RefreshConfirmWindow() { Owner = this })
                        {
                            dlg.ShowExitButton = _alwaysAllowExit;
                            dlg.ShowDialog();
                            var choice = dlg.Choice;
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
                            else if (choice == RefreshChoice.Exit)
                            {
                                log.Info("User chose: Exit — showing confirmation");
                                using (var exitDlg = new ExitConfirmWindow() { Owner = this })
                                {
                                    exitDlg.PromptText = _exitConfirmPrompt;
                                    exitDlg.ShowDialog();
                                    if (exitDlg.Confirmed)
                                    {
                                        log.Info("Exit confirmed — shutting down");
                                        System.Windows.Application.Current.Shutdown();
                                    }
                                    else
                                    {
                                        log.Info("Exit cancelled by user");
                                    }
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

        /// <summary>
        /// Legacy osk.exe accessibility fallback. Only called when EnableOskFallback=true and
        /// the Win11 touch keyboard (TouchKeyboardService) is unavailable.
        /// </summary>
        private void ShowAccessibilityOsk()
        {
            try
            {
                if (TryRestoreOskWindow())
                {
                    log.Info("Restored existing OSK window (accessibility fallback)");
                    return;
                }

                // Kill stale instances then relaunch
                foreach (var p in Process.GetProcessesByName("osk"))
                {
                    try { p.Kill(true); p.WaitForExit(1000); } catch { }
                }

                Process.Start(new ProcessStartInfo { FileName = "osk.exe", UseShellExecute = true });
                log.Info("Launched accessibility OSK fallback (osk.exe)");
            }
            catch (Exception ex)
            {
                log.Warn($"ShowAccessibilityOsk failed: {ex.Message}");
            }
        }

        private bool TryRestoreOskWindow()
        {
            try
            {
                var oskWindow = FindWindow("OSKMainClass", null);
                if (oskWindow == IntPtr.Zero) return false;
                try { ShowWindow(oskWindow, SW_RESTORE); } catch { }
                try { ShowWindow(oskWindow, SW_SHOW); } catch { }
                return true;
            }
            catch { return false; }
        }
    }
}
