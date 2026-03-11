using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using log4net;

namespace BrowserHost.Services
{
    /// <summary>
    /// Toggles the Windows 11 Touch Keyboard (TabTip / TextInputHost) via ITipInvocation COM interop.
    ///
    /// Old flow: JS injected into WebView posted SHOW_OSK → ShowOnScreenKeyboard() → TabTip.exe + forced osk.exe
    /// New flow: Bottom-bar Keyboard button → TouchKeyboardService.Toggle() → ITipInvocation COM → TabTip.exe fallback only
    ///
    /// Does NOT launch osk.exe unless the caller explicitly handles an EnableOskFallback path.
    /// </summary>
    public sealed class TouchKeyboardService
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(TouchKeyboardService));

        // CLSID and IID for the Win11 touch keyboard toggle API
        private static readonly Guid TipInvocationClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");

        [ComImport]
        [Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITipInvocation
        {
            void Toggle(IntPtr hwnd);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private static readonly string TabTipPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "microsoft shared", "ink", "TabTip.exe");

        /// <summary>
        /// Returns the screen-coordinate bounding rect of the visible touch keyboard, or null if not open/visible.
        /// Checks Win11 TextInputHost (Windows.UI.Core.CoreWindow) and classic IPTip_Main_Window.
        /// </summary>
        public Rect? GetKeyboardRect()
        {
            // Win11 TextInputHost
            var hwnd = FindWindow("Windows.UI.Core.CoreWindow", "Microsoft Text Input Application");
            // Classic touch keyboard / TabTip
            if (hwnd == IntPtr.Zero) hwnd = FindWindow("IPTip_Main_Window", null);
            if (hwnd == IntPtr.Zero) hwnd = FindWindow("IPTIP_Main_Window", null);

            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                return null;

            if (!GetWindowRect(hwnd, out var r))
                return null;

            // Reject degenerate or collapsed rects
            if (r.Right <= r.Left || r.Bottom <= r.Top)
                return null;

            return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>Gets whether the touch keyboard is currently visible on screen.</summary>
        public bool IsOpen => GetKeyboardRect().HasValue;

        /// <summary>
        /// Toggles the touch keyboard via ITipInvocation COM.
        /// If COM fails, falls back to launching TabTip.exe (which will show the keyboard on next toggle).
        /// Returns true if the toggle action was invoked successfully.
        /// </summary>
        public bool Toggle(IntPtr ownerHwnd)
        {
            if (TryComToggle(ownerHwnd))
                return true;

            log.Warn("COM ITipInvocation.Toggle failed; falling back to TabTip.exe launch");
            return TryLaunchTabTip();
        }

        private bool TryComToggle(IntPtr ownerHwnd)
        {
            try
            {
                var type = Type.GetTypeFromCLSID(TipInvocationClsid, throwOnError: false);
                if (type == null)
                {
                    log.Warn("ITipInvocation CLSID not registered on this system (Touch Keyboard service may be disabled)");
                    return false;
                }

                var instance = Activator.CreateInstance(type);
                if (instance is ITipInvocation tip)
                {
                    tip.Toggle(ownerHwnd);
                    log.Info("ITipInvocation.Toggle succeeded");
                    return true;
                }

                log.Warn("Created COM instance but could not cast to ITipInvocation");
                return false;
            }
            catch (Exception ex)
            {
                log.Warn($"ITipInvocation.Toggle threw: {ex.Message}");
                return false;
            }
        }

        private bool TryLaunchTabTip()
        {
            var path = File.Exists(TabTipPath) ? TabTipPath
                       : @"C:\Program Files\Common Files\Microsoft Shared\ink\TabTip.exe";

            if (!File.Exists(path))
            {
                log.Warn($"TabTip.exe not found at: {path}. Touch Keyboard service may not be installed.");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                log.Info($"Launched TabTip.exe from {path}");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to launch TabTip.exe: {ex.Message}");
                return false;
            }
        }
    }
}
