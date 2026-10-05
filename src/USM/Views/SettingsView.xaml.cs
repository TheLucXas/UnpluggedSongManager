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
    }

    public class StatusTextConverter : IValueConverter
    {
        public static readonly StatusTextConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool b && b) ? "✓ Ready / Installed" : "✗ Not Found / Missing";
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
