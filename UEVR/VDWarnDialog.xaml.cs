using System.Windows;
using System.Windows.Input;

namespace UEVR
{
    public partial class VDWarnDialog : Window
    {
        public bool HideFutureWarnings { get; private set; }
        public bool DialogResultOK { get; private set; }

        public VDWarnDialog(string? text = null)
        {
            InitializeComponent();
            if ( text is null )
            {
                m_textBoxVD.Text = "Virtual Desktop has been detected running.\r\nMake sure you use OpenXR for the least issues.\r\n";
            }
            else m_textBoxVD.Text = text;

        }



        private void btnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResultOK = true;
            this.Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResultOK = false;
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void chkHideWarning_Checked(object sender, RoutedEventArgs e) {
            ((MainWindow)(this.Owner)).UpdateSetting("IgnoreFutureVDWarnings", true);
        }

        private void chkHideWarning_Unchecked(object sender, RoutedEventArgs e) {
            ((MainWindow)(this.Owner)).UpdateSetting("IgnoreFutureVDWarnings", false);

        }
    }
}
