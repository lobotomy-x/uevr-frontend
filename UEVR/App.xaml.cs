using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UEVR
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private DispatcherTimer? _backgroundTimer;
        protected override async void OnStartup(StartupEventArgs e)
        {
            var procs = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
            if (procs.Length > 1) {
                Process.GetCurrentProcess().Close();
            }
            AppDomain.CurrentDomain.FirstChanceException += (s, ex) =>
            {
                Debug.WriteLine($"FirstChance: {ex.Exception.Message}");
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                Debug.WriteLine($"UnhandledException: {ex.ExceptionObject}");
            };

            DispatcherUnhandledException += (s, ex) =>
            {
                Debug.WriteLine($"DispatcherUnhandledException: {ex.Exception.Message}");
                // optionally set ex.Handled = true; during diagnostics
            };

            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Debug.WriteLine($"UnobservedTaskException: {ex.Exception.Message}");
            };

            //Start a background timer that will invoke periodic updates on the main window even when it's hidden
            _backgroundTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _backgroundTimer.Tick += BackgroundTimer_Tick;
            _backgroundTimer.Start();

            base.OnStartup(e);
        }

        private async void BackgroundTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (Current?.MainWindow is MainWindow mw)
                {
                   await mw.Update();
                }
            }
            catch (Exception ex)
            {

            }
        }
    }
}
