using System;
using System.Windows;
using System.Windows.Input;

namespace BrowserHost
{
    public partial class PullTabWindow : Window
    {
        public event Action? TabClicked;

        public PullTabWindow()
        {
            InitializeComponent();
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            MouseEnteredTab?.Invoke();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            MouseLeftTab?.Invoke();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            TabClicked?.Invoke();
        }

        public event Action? MouseEnteredTab;
        public event Action? MouseLeftTab;
    }
}
