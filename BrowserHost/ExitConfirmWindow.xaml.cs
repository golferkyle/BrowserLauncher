using System;
using System.Windows;
using System.Windows.Input;

namespace BrowserHost
{
    public partial class ExitConfirmWindow : Window, IDisposable
    {
        private bool _disposed = false;
        public bool Confirmed { get; private set; } = false;

        public string PromptText
        {
            set => PromptTextBlock.Text = value;
        }

        public ExitConfirmWindow()
        {
            InitializeComponent();
            Loaded += ExitConfirmWindow_Loaded;
        }

        private void ExitConfirmWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                BtnYes?.ReleaseMouseCapture();
                BtnNo?.ReleaseMouseCapture();
                this.Focus();
                Keyboard.ClearFocus();
            }
            catch { }
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
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
                try { Loaded -= ExitConfirmWindow_Loaded; } catch { }
                try { if (BtnYes != null) BtnYes.Click -= BtnYes_Click; } catch { }
                try { if (BtnNo != null) BtnNo.Click -= BtnNo_Click; } catch { }
            }
            _disposed = true;
        }
    }
}
