using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using USM.Core.Services.Diagnostics;
using USM.Core.Services.Localization;
using USM.Core.Services.Quest;

namespace USM.Views
{
    /// <summary>Shows a generated bug report for the user to review and copy. Nothing is sent automatically.</summary>
    public partial class ReportBugWindow : Window
    {
        public const string IssueUrl = "https://github.com/TheLucXas/UnpluggedSongManager/issues/new";
        private readonly Func<BugReportResult> _factory;
        private bool _started;

        /// <summary>Builds the real report off the UI thread when the window loads.</summary>
        public ReportBugWindow() : this(() => BugReportBuilder.Build(BugReportBuilder.DefaultMaxChars, QuestService.GetQuestToolsCacheDirectory())) { }

        /// <summary>Uses a custom report source (tests).</summary>
        public ReportBugWindow(Func<BugReportResult> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            InitializeComponent();
            ReportBox.Text = LocalizationService.T("ReportBug_Generating");
            Loaded += async (_, _) => await GenerateAsync();
        }

        public string ReportText => ReportBox.Text;

        /// <summary>Generates the report off the UI thread (also called from Loaded; runs once).</summary>
        public async Task GenerateAsync()
        {
            if (_started) return;
            _started = true;
            try
            {
                var result = await Task.Run(_factory);
                ReportBox.Text = result.Text;
                CopyButton.IsEnabled = !string.IsNullOrEmpty(result.Text);
                if (result.WasTrimmed) ShowStatus(LocalizationService.T("ReportBug_Trimmed"), false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("ReportBug", "Report generation failed: " + ex.GetType().Name);
                ReportBox.Text = LocalizationService.T("ReportBug_Failed", ex.Message);
                CopyButton.IsEnabled = false;
            }
        }

        private void ShowStatus(string text, bool ok)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource(ok ? "BrushAccentGreen" : "BrushWarning");
            StatusText.Visibility = Visibility.Visible;
        }

        private async void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            string text = ReportBox.Text;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    ShowStatus(LocalizationService.T("ReportBug_Copied"), true);
                    return;
                }
                catch (Exception)
                {
                    await Task.Delay(100);
                }
            }
            DiagnosticLog.Warn("ReportBug", "Clipboard unavailable after retries.");
            ShowStatus(LocalizationService.T("ReportBug_CopyFailed"), false);
        }

        private void IssueButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = IssueUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("ReportBug", "Could not open browser: " + ex.GetType().Name);
                ShowStatus(LocalizationService.T("ReportBug_OpenIssueFailed", IssueUrl), false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
