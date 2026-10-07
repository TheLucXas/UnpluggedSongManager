using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace USM.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void MaxDownloads_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        private void MaxDownloads_Pasting(object sender, System.Windows.DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9]+$"))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void MaxDownloads_LostFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SettingsViewModel vm)
            {
                if (!int.TryParse(vm.MaxDownloadsText, out int val) || val < 1)
                {
                    vm.MaxDownloadsText = "1";
                }
                else if (val > 99)
                {
                    vm.MaxDownloadsText = "99";
                }
            }
        }
    }

    public class StatusTextConverter : IValueConverter
    {
        public static readonly StatusTextConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) 
                ? USM.Core.Services.Localization.LocalizationService.T("Settings_StatusReady") 
                : USM.Core.Services.Localization.LocalizationService.T("Settings_StatusMissing");
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class StatusColorConverter : IValueConverter
    {
        public static readonly StatusColorConverter Instance = new();
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(16, 185, 129));
        private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(245, 158, 11));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) ? GreenBrush : OrangeBrush;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
