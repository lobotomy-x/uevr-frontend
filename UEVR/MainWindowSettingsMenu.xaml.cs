using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Policy;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UEVR.Utils;
using static UEVR.Utils.GitAPI;
using static UEVR.Utils.ProcessManagement;
using static UEVR.Utils.ShortcutHelper;
using File = System.IO.File;
using static UEVR.Utils.BrowserHelper;
using Microsoft.VisualBasic;

/*
    Copyright lobotomyx 2025-2026
    This file, the corresponding XAML class, and all code within fall under MIT License and are free to use, modify, distribute, etc. under those terms. 
    The same terms may or may not apply to the rest of the repository which is owned and maintained by praydog.
*/

namespace UEVR {
    public partial class MainWindowSettingsMenu : UserControl {
        private readonly MainWindow window;
        private readonly string _releasesPage = "https://api.github.com/repos/praydog/uevr-nightly/releases";
        private List<GitHubResponseObject> _releases = new();
        private GitHubResponseObject? _selectedRelease;
        private GitHubResponseObject? _currentRelease;
        private static readonly HttpClient _httpClient = new HttpClient();
        private string epicPath = "C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win64\\EpicGamesLauncher.exe";
        private string steamPath = "C:\\Program Files (x86)\\Steam\\Steam.exe";
        private bool _launchersOpened;

        private readonly Updater _updater;



        public MainWindowSettingsMenu() {
            InitializeComponent();
            window = (MainWindow)Application.Current.MainWindow;
            m_nullifyVRPluginsCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.NullifyVRPlugins), true);
            m_nullifyVRPluginsCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.NullifyVRPlugins), false);

            m_ignoreVDWarningsCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.IgnoreFutureVDWarnings), true);
            m_ignoreVDWarningsCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.IgnoreFutureVDWarnings), false);

            m_focusGameOnInjectionCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.FocusGameOnInjection), true);
            m_focusGameOnInjectionCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.FocusGameOnInjection), false);

            m_openvrRadio.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.OpenVRRadio), true);
            m_openxrRadio.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.OpenXRRadio), true);

            m_directCloseCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.CloseFromWindow), true);
            m_directCloseCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.CloseFromWindow), false);

            m_openToTrayCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.OpenToTray), true);
            m_openToTrayCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.OpenToTray), false);

            m_autoUpdateCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutomaticNightlyUpdates), true);
            m_autoUpdateCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutomaticNightlyUpdates), false);

            m_autoInjectCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutomaticInjection), true);
            m_autoInjectAlwaysCheckbox.Checked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutoInjectNewGames), true);

            m_autoInjectCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutomaticInjection), false);
            m_autoInjectAlwaysCheckbox.Unchecked += (s, e) => UpdateSetting(nameof(MainWindowSettings.AutoInjectNewGames), false);
            _updater = new Updater();

            var _UEVRLnkPath = GetShellStartupPath("UEVR.lnk");
            if (File.Exists(_UEVRLnkPath)) {
                m_startWithWindowsCheckbox.IsChecked = true;
                var _UEVRTargetPath = GetShortcutTarget(_UEVRLnkPath);
                if (_UEVRTargetPath is not null) {
                    if (_UEVRTargetPath != Environment.ProcessPath) {
                        UpdateShortcutTarget(_UEVRLnkPath, Environment.ProcessPath);
                    }
                }
            }
            //if (IsCurrentProcessElevated()) {
            //    m_launchHelper.Visibility = Visibility.Visible;

            //    m_launchHelper.Click += (s, e) => {
            //        ProcessStartInfo ps = new ProcessStartInfo() {
            //            FileName = "UEVRHelper.exe",
            //            WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            //        };
            //        Process.Start(ps);
            //    };
            //} else {
            //    m_launchHelper.Visibility = Visibility.Collapsed;
            //}


        }




        public void Localize(string? language) {
            if (language is null) language = "en";

        }

        private void UpdateSetting(string propertyName, object value) {
            if (this.DataContext is MainWindowSettings settings) {
                settings [propertyName] = value;
                try {
                    settings.Save();
                    try { window?.UpdateMainWindowSettings(); } catch { }
                } catch { }
            }
        }


        private void Close_Click(object sender, RoutedEventArgs e) {
            if (this.DataContext is MainWindowSettings settings) {
                settings.IsMenuOpen = false;
            }
        }

        private void m_startWithWindowsCheckbox_Checked(object sender, RoutedEventArgs e) {
            var shortcut = GetShellStartupPath("UEVR.lnk");
            if (File.Exists(shortcut)) return;
            var mainModule = Process.GetCurrentProcess().MainModule;
            var exePath = mainModule!.FileName;
            CreateShortcutNative(shortcut, exePath!);
        }


        private void m_startWithWindowsCheckbox_Unchecked(object sender, RoutedEventArgs e) {
            var shortcut = GetShellStartupPath("UEVR.lnk");
            if (File.Exists(shortcut)) {
                DeleteShortcut(shortcut);
            }
        }

        private void m_startSteamWithWindowsCheckbox_Checked(object sender, RoutedEventArgs e) {
            if (!File.Exists(steamPath)) return;
            CreateUnelevatedShortcut(steamPath, GetShellStartupPath("Steam.lnk"));
        }

        private void m_startSteamWithWindowsCheckbox_Unchecked(object sender, RoutedEventArgs e) {
            var shortcut = GetShellStartupPath("Steam.lnk");
            if (File.Exists(shortcut)) {
                DeleteShortcut(shortcut);
            }
        }

        private void m_startEpicWithWindowsCheckbox_Checked(object sender, RoutedEventArgs e) {
            if (!File.Exists(epicPath)) return;
            CreateUnelevatedShortcut(epicPath, GetShellStartupPath("EpicGamesLauncher.lnk"));
        }

        private void m_startEpicWithWindowsCheckbox_Unchecked(object sender, RoutedEventArgs e) {
            var shortcut = GetShellStartupPath("EpicGamesLauncher.lnk");
            if (File.Exists(shortcut)) {
                DeleteShortcut(shortcut);
            }
        }

        private void m_launchSteamUnelevatedClick(object sender, RoutedEventArgs e) {
            if (IsProcessRunning("Steam", out Process? p)) {
                if (!IsCurrentProcessElevated() && IsProcessElevated(p)) {
                    TerminateProcessNative(p!.Id);
                }
            }
            LaunchProcessUnelevated(steamPath);
        }

        private void m_launchEpicUnelevatedClick(object sender, RoutedEventArgs e) {
            if (IsProcessRunning("EpicGamesLauncher", out Process? p)) {
                if (!IsCurrentProcessElevated() && IsProcessElevated(p)) {
                    TerminateProcessNative(p.Id);
                }
            }
            LaunchProcessUnelevated(epicPath);
        }




        private void UserControl_Loaded(object sender, RoutedEventArgs e) {
            window.SettingsMenuOpen = true;
        }



        private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (e.AddedItems.Count == 0) return;
            if (e.AddedItems [0] is TabItem _tab && _tab.Header.ToString() == "Launchers" && !_launchersOpened) {
                LaunchersTab();
            }
            if (e.AddedItems [0] is not TabItem tab || tab.Header.ToString() != "Updates") return;
            
            _releases = await _updater.GetReleasesAsync();
            VersionDateCombo.ItemsSource = _releases;
            if (VersionDateCombo.Items.Count > 0)
                VersionDateCombo.SelectedIndex = 0;
            SetCurrentRelease();
        }

        private async void SetCurrentRelease() {
            // Show current version from revision.txt
            try {
                var revisionPath = Path.Combine(MainWindow.GetGlobalDir(), "UEVR", "revision.txt");
                var revision = File.ReadAllText(revisionPath).Trim();
                foreach (var rel in _releases) {
                    if (rel.Tag_Name!.Contains(revision, StringComparison.InvariantCultureIgnoreCase)) {
                        _currentRelease = rel;
                        break;
                    }
                }
                var releaseText = _currentRelease!.Name!.Split($" ({revision})").First();
                CurrentVersion.Text = "Installed:\n" + releaseText.ToString() +
                    " (" + _currentRelease!.Published_At?.ToString("MMMM dd, yyyy") + ")" ?? "Unknown";
                var latestdate = _releases.OrderByDescending(r => r.Published_At).First().Published_At!.Value;
                var currentdate = _currentRelease!.Published_At!.Value;
                if (DateTime.Compare(currentdate, latestdate)  < 0) {
                    var diff = latestdate.Subtract(currentdate);
                    CurrentVersion.Text += "\n" + $"{diff.Days.ToString()} Days behind";
                }    
            } catch {
                CurrentVersion.Text = "Current: Unknown";
            }
        }

        private void VersionDateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            _selectedRelease = VersionDateCombo.SelectedItem as GitHubResponseObject;
            btnSelectVersion.IsEnabled = _selectedRelease != null;
            btnOpenGithub.IsEnabled = _selectedRelease != null;
        }

        private async void btnSelectVersion_Click(object sender, RoutedEventArgs e) {
            if (_selectedRelease == null) return;
            DownloadStatusText.Text = "Downloading...";
            var success = await _updater.DownloadAndExtractAsync(_selectedRelease).ConfigureAwait(false);
            DownloadStatusText.Text = success ? "✅ Update installed successfully!" : "❌ Failed";
            SetCurrentRelease();
        }

        private void btnOpenGithub_Click(object sender, RoutedEventArgs e) {
            if (_selectedRelease is null) return;
            LaunchURL("https://github.com/praydog/uevr-nightly/releases/tag/" + _selectedRelease.Tag_Name!.ToString());
        }

        private void LaunchersTab() {
            // Update text and if the paths are nondefault use those
            if (IsProcessRunning("Steam", out Process? p, out string? path)) {
                if (IsProcessElevated(p)) {
                    m_launchSteamUnelevatedBtn.Content = "Relaunch Steam";
                    if (path is not null && steamPath != path) {
                        steamPath = path;
                    }
                }
            }

            if (IsProcessRunning("EpicGamesLauncher", out Process? proc, out string? epic_path)) {
                if (IsProcessElevated(proc)) {
                    m_launchEpicUnelevatedBtn.Content = "Relaunch Epic";
                    if (epic_path is not null && epicPath != epic_path) {
                        epicPath = epic_path;
                    }
                }
            }

            // Only really need to update these settings when they're displayed
            var _epicLnkPath = GetShellStartupPath("EpicGamesLauncher.lnk");
            if (File.Exists(_epicLnkPath)) {
                m_startEpicWithWindowsCheckbox.IsChecked = true;
                var epicArgs = GetShortcutArguments(_epicLnkPath);
                var _epicTargetPath = Path.GetFullPath(epicArgs!.Split(" ").Last());
                if (_epicTargetPath is not null) {
                    if (_epicTargetPath != epicPath) {
                        if (File.Exists(_epicTargetPath)) {
                            epicPath = _epicTargetPath;
                        } else if (File.Exists(epicPath)) {
                            UpdateShortcutTarget(_epicLnkPath, epicPath);
                        } else {
                            DeleteShortcut(_epicLnkPath);
                            m_startEpicWithWindowsCheckbox.IsChecked = false;
                        }
                    }
                }
            }

            var _SteamLnkPath = GetShellStartupPath("Steam.lnk");
            if (File.Exists(_SteamLnkPath)) {
                m_startSteamWithWindowsCheckbox.IsChecked = true;
                var steamArgs = GetShortcutArguments(_SteamLnkPath);

                var _SteamTargetPath = Path.GetFullPath(steamArgs!.Split(" ").Last());
                if (_SteamTargetPath is not null) {
                    if (_SteamTargetPath != steamPath) {
                        if (File.Exists(_SteamTargetPath)) {
                            steamPath = _SteamTargetPath;
                        } else if (File.Exists(steamPath)) {
                            UpdateShortcutTarget(_SteamLnkPath, steamPath);
                        } else {
                            DeleteShortcut(_SteamLnkPath);
                            m_startSteamWithWindowsCheckbox.IsChecked = false;
                        }
                    }
                }
            }
        }



        private void UserControl_Unloaded(object sender, RoutedEventArgs e) {
            window.SettingsMenuOpen = false;
        }

        private void m_closeUEVRBtn_Click(object sender, RoutedEventArgs e) {
            window.Close();
        }

        private async void m_checkUpdatesBtn_Click(object sender, RoutedEventArgs e) {
            await window.CheckForNightlyUpdates();
        }

        private void m_resetAllBtn_Click(object sender, RoutedEventArgs e) {
            window.ResetSettings();
        }

        private void m_closeMenuBtnClick(object sender, RoutedEventArgs e) {
            // Persist the correct settings property name (IsMenuOpen) to avoid
            // saving an unknown setting key which can throw.
            UpdateSetting(nameof(MainWindowSettings.IsMenuOpen), false);
        }
    }
}