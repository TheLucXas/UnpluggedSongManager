using System;
using System.Windows;
using System.Windows.Controls;
using USM.ViewModels;

namespace USM.Views
{
    public partial class ConverterView : UserControl
    {
        public ConverterView()
        {
            InitializeComponent();
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && DataContext is ConverterViewModel vm)
                {
                    vm.AddFilesToQueue(files);
                }
                e.Handled = true;
            }
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            LogTextBox.ScrollToEnd();
        }
    }
}
