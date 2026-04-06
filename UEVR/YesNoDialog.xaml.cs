using System.Windows;
using System.Windows.Input;

namespace UEVR
{
    public partial class YesNoDialog : Window
    {
        public bool DialogResultYes { get; private set; } = false;
        public bool ShiftHeld { get; private set; } = false;   
        public string? AltYesText { get; set; } 
        public string? AltNoText { get; set; }

        public YesNoDialog(string windowTitle, string txt)
        {
            InitializeComponent();
            m_dialogText.Text = txt;
            this.Title = windowTitle;
        }

        private void btnYes_Click(object sender, RoutedEventArgs e)
        {
            DialogResultYes = true;
            this.Close();
        }

        private void btnNo_Click(object sender, RoutedEventArgs e)
        {
            DialogResultYes = false;
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void shift_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftShift)
            {
                ShiftHeld = true; 
                if (AltNoText is not null )
                {
                    btnNo.Content = AltNoText.ToString();
                }
                if ( AltYesText is not null )
                {
                    btnYes.Content = AltYesText.ToString();
                }
            }
        }

        private void shift_KeyUp(object sender, KeyEventArgs e)
        {
            if ( e.Key == Key.LeftShift )
            {
                ShiftHeld = false;
                btnNo.Content = "No";
                btnYes.Content = "Yes";
            }
        }
    }
}
