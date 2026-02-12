using System;
using System.Windows;
using System.Windows.Input;

namespace BrowserHost
{
    public enum RefreshChoice
    {
        Refresh,
        Cancel,
        Home
    }

    public partial class RefreshConfirmWindow : Window, IDisposable
    {
        private bool _disposed = false;
        public RefreshChoice Choice { get; private set; } = RefreshChoice.Cancel;

        public RefreshConfirmWindow()
        {
            InitializeComponent();
            Loaded += RefreshConfirmWindow_Loaded;
        }

        private void RefreshConfirmWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // Ensure buttons have their default visuals and no lingering focus
            try
            {
                // release any mouse/touch capture so buttons don't stay visually pressed
                BtnRefresh?.ReleaseMouseCapture();
                BtnCancel?.ReleaseMouseCapture();
                BtnHome?.ReleaseMouseCapture();
                // move focus to window so buttons don't remain pressed
                this.Focus();
                Keyboard.ClearFocus();
            }
            catch { }
        }

        private void ResetButtonVisuals()
        {
            try
            {
                BtnRefresh?.ReleaseMouseCapture();
                BtnCancel?.ReleaseMouseCapture();
                BtnHome?.ReleaseMouseCapture();

                // Toggle IsEnabled to force visual state refresh
                if (BtnRefresh != null)
                {
                    BtnRefresh.IsEnabled = false;
                    Dispatcher.BeginInvoke(new Action(() => BtnRefresh.IsEnabled = true), System.Windows.Threading.DispatcherPriority.Input);
                }
                if (BtnCancel != null)
                {
                    BtnCancel.IsEnabled = false;
                    Dispatcher.BeginInvoke(new Action(() => BtnCancel.IsEnabled = true), System.Windows.Threading.DispatcherPriority.Input);
                }
                if (BtnHome != null)
                {
                    BtnHome.IsEnabled = false;
                    Dispatcher.BeginInvoke(new Action(() => BtnHome.IsEnabled = true), System.Windows.Threading.DispatcherPriority.Input);
                }

                // Clear focus as well
                Keyboard.ClearFocus();
            }
            catch { }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(RefreshChoice.Refresh, true);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(RefreshChoice.Cancel, false);
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            CloseWithChoice(RefreshChoice.Home, false);
        }

        private void CloseWithChoice(RefreshChoice choice, bool? dialogResult)
        {
            try
            {
                Choice = choice;

                if (BtnRefresh != null) BtnRefresh.IsEnabled = false;
                if (BtnCancel != null) BtnCancel.IsEnabled = false;
                if (BtnHome != null) BtnHome.IsEnabled = false;

                try { BtnRefresh?.ReleaseMouseCapture(); } catch { }
                try { BtnCancel?.ReleaseMouseCapture(); } catch { }
                try { BtnHome?.ReleaseMouseCapture(); } catch { }
                try { Mouse.Capture(null); } catch { }
                try { Keyboard.ClearFocus(); } catch { }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (dialogResult.HasValue)
                        {
                            DialogResult = dialogResult.Value;
                        }
                        Close();
                    }
                    catch
                    {
                        try { Close(); } catch { }
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch
            {
                try { Close(); } catch { }
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            // Ensure visuals are reset after closing
            ResetButtonVisuals();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                try { Loaded -= RefreshConfirmWindow_Loaded; } catch { }
                try { if (BtnRefresh != null) BtnRefresh.Click -= BtnRefresh_Click; } catch { }
                try { if (BtnCancel != null) BtnCancel.Click -= BtnCancel_Click; } catch { }
                try { if (BtnHome != null) BtnHome.Click -= BtnHome_Click; } catch { }
            }
            _disposed = true;
        }
    }
}
