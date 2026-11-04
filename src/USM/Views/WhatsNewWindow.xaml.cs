using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using USM.Core.Services;
using USM.Core.Services.Localization;

namespace USM.Views
{
    /// <summary>Scrollable changelog, newest version first.</summary>
    public partial class WhatsNewWindow : Window
    {
        public WhatsNewWindow() : this(WhatsNewService.Entries) { }

        public WhatsNewWindow(IReadOnlyList<ChangelogEntry> entries)
        {
            InitializeComponent();
            foreach (var entry in entries)
            {
                EntriesPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.T("WhatsNew_VersionHeader", entry.Version),
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("BrushAccentBlue"),
                    Margin = new Thickness(0, 8, 0, 6),
                    TextWrapping = TextWrapping.Wrap,
                });
                foreach (string key in entry.Keys)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var bullet = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), Foreground = (Brush)FindResource("BrushAccentPink") };
                    var text = new TextBlock { Text = LocalizationService.T(key), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("BrushTextPrimary") };
                    Grid.SetColumn(text, 1);
                    row.Children.Add(bullet);
                    row.Children.Add(text);
                    EntriesPanel.Children.Add(row);
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
