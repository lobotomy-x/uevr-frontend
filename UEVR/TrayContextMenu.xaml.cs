using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

/*
    Copyright lobotomyx 2025-2026
    This file, the corresponding XAML class, and all code within fall under MIT License and are free to use, modify, distribute, etc. under those terms. 
    The same terms may or may not apply to the rest of the repository which is owned and maintained by praydog.
*/



namespace UEVR
{
    public partial class TrayContextMenu : Window
    {
        private bool _connected;
        private bool _paused;
        private bool _isClosing;
        private MainWindow _ownerWindow;
        // Connection state must be passed to scale properly with dynamic menu items
        public TrayContextMenu(MainWindow owner, bool connectedState, bool pausedState)
        {
            InitializeComponent();

            // Keep a strong reference to the real owner window and set WPF owner (don't replace Application.Current.MainWindow)
            // Keep a strong reference to the real owner window and set WPF owner
            _ownerWindow = owner ?? throw new ArgumentNullException(nameof(owner));
            this.Owner = owner;

            SetState(connectedState, pausedState);


        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDesktopWindow();


        internal void ShowAtMouse(NotifyIcon.Win32Point mousePos)
        {
            // wtf was wrong with microsoft when they came up with wpf

            // Get DPI scaling factors for the current display
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

            // Ensure the window's size is calculated before positioning
            this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            this.Arrange(new Rect(0, 0, this.DesiredSize.Width, this.DesiredSize.Height));
            this.UpdateLayout();

            // Convert to WPF DIPs
            double x = mousePos.X / dpiX;
            double y = mousePos.Y / dpiY;

            var Right = SystemParameters.WorkArea.Width;
            var Bottom = SystemParameters.WorkArea.Height;

            double w = this.ActualWidth > 0 ? this.ActualWidth : this.DesiredSize.Width;
            double h = this.ActualHeight > 0 ? this.ActualHeight : this.DesiredSize.Height;

            // Prefer placing the menu to the left/top of the cursor so it doesn't overflow off-screen
            double left = x - w;
            double top = y - h;

            // Clamp to work area
            left = Math.Max(0, Math.Min(left, Right - w));
            top = Math.Max(0, Math.Min(top, Bottom - h));

            this.Left = left;
            this.Top = top;

            // draw the window now
            this.Show();
            // bring to front and focus to allow pressing tab and then using keyboard controls
            this.Activate();
            this.Focus();
        }

        internal void ShowAtIcon(NotifyIcon.RECT iconLocation)
        {

            // wtf was wrong with microsoft when they came up with wpf
            // Get DPI scaling factors for the current display
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget.TransformToDevice.M22 ?? 1.0;

            // Ensure the window's size is calculated before positioning
            this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            this.Arrange(new Rect(0, 0, this.DesiredSize.Width, this.DesiredSize.Height));
            this.UpdateLayout();

            // Convert icon rect to WPF DIPs
            double iconX = iconLocation.left / dpiX;
            double iconY = iconLocation.top / dpiY;

            var Right = SystemParameters.WorkArea.Width;
            var Bottom = SystemParameters.WorkArea.Height;

            double w = this.ActualWidth > 0 ? this.ActualWidth : this.DesiredSize.Width;
            double h = this.ActualHeight > 0 ? this.ActualHeight : this.DesiredSize.Height;

            // Place the menu above and to the left of the icon by default, clamped to screen
            double left = iconX - w;
            double top = iconY - h;
            left = Math.Max(0, Math.Min(left, Right - w));
            top = Math.Max(0, Math.Min(top, Bottom - h));

            this.Left = left;
            this.Top = top;

            // draw the window now
            this.Show();
            // bring to front and focus to allow pressing tab and then using keyboard controls
        //   this.Activate();
           //this.Focus();
        }

        // We need to offset the window from the actual mouse point to prevent clipping at screen edge
        // most context menus tend to go to the right of the icon but steam and virtual desktop both go to the left
        // so that seems the most fitting, therefore we must offset by the actual window size
        // but WPF only calculates this upon drawing the window so to avoid hard coding an offset we use the Loaded event
        // which will autorun after we call Show
        private void Window_Loaded(object sender, RoutedEventArgs e) {
            //WindowInteropHelper helper = new WindowInteropHelper(this);
            //var parent = GetParent(helper.Handle);
            //SetParent(helper.Handle, IntPtr.Zero);
            //BringWindowToTop(helper.Handle);
            //SwitchToThisWindow(helper.Handle, true);
   
        }

        internal void Update() {
           // WindowInteropHelper helper = new WindowInteropHelper(this);
            // BringWindowToTop(helper.Handle);

        }

        // Just kidding we are gonna call the offset from the notifyicon      
        internal void OffsetWindowPos()
        {
            this.Top -= (this.ActualHeight * 0.85);
            this.Left -= (this.ActualWidth * 0.85);
        }

        // we remake the contextmenu window as needed and lose the state each time its closed 
        // doing it this way instead of persisting data given that users could have the notifyicon
        // open for days at a time and I don't want to do some arbitrary timed cleanup
        internal void SetState(bool connectedState, bool pausedState)
        {
            this._connected = connectedState;
            this._paused = pausedState;



            if (this._connected)
            {
                btnResume.Visibility = Visibility.Collapsed;
                btnPause.Visibility = Visibility.Collapsed;
                btnGameFolder.Visibility = Visibility.Visible;
                btnProfileFolder.Visibility = Visibility.Visible;
                btnGlobalFolder.Visibility = Visibility.Collapsed;

                btnCloseGame.Visibility = Visibility.Visible;
            }
            else
            {
                btnGlobalFolder.Visibility = Visibility.Visible;
                if (this._paused) // pausing is only valid while not connected
                {
                    btnResume.Visibility = Visibility.Visible;
                    btnPause.Visibility = Visibility.Collapsed;
                }
                else if (_ownerWindow != null && _ownerWindow.CheckSetting("AutomaticInjection")) // there's also no point in pausing if auto inject is off 
                {
                    btnResume.Visibility = Visibility.Collapsed;
                    btnPause.Visibility = Visibility.Visible;
                }
                btnGameFolder.Visibility = Visibility.Collapsed;
                btnCloseGame.Visibility = Visibility.Collapsed;
            }
        }

        private void btnExit_Click(object sender, RoutedEventArgs e)
        {
            // Close the real owner window
            try { _ownerWindow?.Close(); } catch { }
        }

        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.PauseAutoInject();
            this.btnResume.Visibility = Visibility.Visible;
            this.btnPause.Visibility = Visibility.Collapsed;
        }


        private void btnResume_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.UnPauseAutoInject();
            this.btnResume.Visibility = Visibility.Collapsed;
            this.btnPause.Visibility = Visibility.Visible;
        }

        private void btnGameFolder_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.OpenGameDir();
        }

        private void btnGlobalFolder_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.OpenGlobalDir();
        }

        private void btnProfileFolder_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.OpenProfileDir();
        }


        private void btnCloseGame_Click(object sender, RoutedEventArgs e)
        {
            _ownerWindow?.TerminateConnectedProcess();
        }

        private void btnShow_Click(object sender, RoutedEventArgs e)
        {
            try {
                _ownerWindow?.Show();
                _ownerWindow?.Activate();
            } catch { }
        }



        private async void Window_Deactivated(object sender, EventArgs e)
        {
            if (_isClosing) {
                return;
            }

            _isClosing = true;
            //Update();
            await Task.Delay(450);
            if (this.IsActive == false) {
                this.Close();
            } else {
                _isClosing = false;
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            ((MainWindow)(Application.Current.MainWindow)).Show();
            try { ((MainWindow)(Application.Current.MainWindow)).Activate(); } catch { }
            ((MainWindow)(Application.Current.MainWindow)).OpenSettingsMenu();
        }

        private async void Window_LostFocus(object sender, RoutedEventArgs e) {
            if (_isClosing) {
                return;
            }

            _isClosing = true;
            //Update();
            await Task.Delay(450);
            if (this.IsActive == false) {
                this.Close();
            } else {
                _isClosing = false;
            }
        }

        private async void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) {
            if (_isClosing) {
                return;
            }

            _isClosing = true;
            //Update();
            await Task.Delay(500);
            if (this.IsActive == false) {
                this.Close();
            } else {
                _isClosing = false;
            }
        }

        private async void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) {
            if (_isClosing) {
                return;
            }

            _isClosing = true;
            //Update();
            await Task.Delay(500);
            if (this.IsActive == false) {
                this.Close();
            } else {
                _isClosing = false;
            }
        }
    }
}
