using System;
using System.ComponentModel;
using System.Windows;
using USM.Core.Services;

namespace USM
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                AudioPlayerService.Instance.Stop();
            }
            catch { }
            base.OnClosing(e);
        }
    }
}