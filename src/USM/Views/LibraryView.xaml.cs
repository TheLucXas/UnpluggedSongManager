using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace USM.Views
{
    public partial class LibraryView : UserControl
    {
        public static readonly IValueConverter MultiTrackTextConverter = new MultiTrackTextConv();
        public static readonly IValueConverter MultiTrackColorConverter = new MultiTrackColorConv();
        public static readonly IValueConverter PlayIconConverter = new PlayIconConv();

        public LibraryView()
        {
            InitializeComponent();
        }

        private void SongCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.FrameworkElement fe && fe.DataContext is USM.Core.Models.InstalledSong song)
            {
                song.IsSelected = !song.IsSelected;
                if (DataContext is USM.ViewModels.LibraryViewModel vm)
                {
                    vm.UpdateSelectionState();
                }
            }
        }

        private class MultiTrackTextConv : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return (value is bool b && b) ? "🎸 MultiTrack" : "🎵 Stereo";
            }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
        }

        private class MultiTrackColorConv : IValueConverter
        {
            private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(16, 185, 129));
            private static readonly SolidColorBrush BlueBrush = new(Color.FromRgb(0, 194, 203));

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return (value is bool b && b) ? GreenBrush : BlueBrush;
            }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
        }

        private class PlayIconConv : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return (value is bool b && b) ? "⏹" : "▶";
            }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
        }
    }
}
