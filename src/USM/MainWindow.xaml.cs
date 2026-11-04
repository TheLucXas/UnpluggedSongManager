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

        private void ReportBug_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                USM.Core.Services.Diagnostics.DiagnosticLog.Info("ReportBug", "Report dialog opened.");
                new USM.Views.ReportBugWindow { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                USM.Core.Services.Diagnostics.DiagnosticLog.Failure("ReportBug", ex.Message, ex);
            }
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