using System.Windows.Controls;

namespace USM.Views
{
    public partial class QuestView : UserControl
    {
        public QuestView()
        {
            InitializeComponent();
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            LogTextBox.ScrollToEnd();
        }
    }
}
