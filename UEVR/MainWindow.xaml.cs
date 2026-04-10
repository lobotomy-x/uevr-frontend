using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UEVR.Utils;
using static UEVR.GameConfig;
using static UEVR.SharedMemory;
using static UEVR.Utils.Nullables;
using static UEVR.Utils.ProcessManagement;
using static UEVR.Utils.WindowUtils;
using static UEVR.Utils.BrowserHelper;
using Path = System.IO.Path;

namespace UEVR {


    #region settings
    class GameSettingEntry : INotifyPropertyChanged {
        private string _key = "";
        private string _value = "";
        private string _tooltip = "";

        public string Key { get => _key; set => SetProperty(ref _key, value); }
        public string Value {
            get => _value;
            set {
                SetProperty(ref _value, value);
                OnPropertyChanged(nameof(ValueAsBool));
            }
        }

        public string Tooltip { get => _tooltip; set => SetProperty(ref _tooltip, value); }

        public int KeyAsInt { get { return Int32.Parse(Key); } set { Key = value.ToString(); } }
        public bool ValueAsBool {
            get => Boolean.Parse(Value);
            set {
                Value = value.ToString().ToLower();
            }
        }

        public Dictionary<string, string> ComboValues { get; set; } = new Dictionary<string, string>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null) {
            if (Equals(storage, value)) return false;
            if (propertyName == null) return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged(string propertyName) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    };

    enum RenderingMethod {
        [Description("Native Stereo")]
        NativeStereo = 0,
        [Description("Synced Sequential")]
        SyncedSequential = 1,
        [Description("Alternating/AFR")]
        Alternating = 2
    };

    enum SyncedSequentialMethods {
        SkipTick = 0,
        SkipDraw = 1,
    };

    class ComboMapping {

        public static Dictionary<string, string> RenderingMethodValues = new Dictionary<string, string>(){
            {"0", "Native Stereo" },
            {"1", "Synced Sequential" },
            {"2", "Alternating/AFR" }
        };

        public static Dictionary<string, string> SyncedSequentialMethodValues = new Dictionary<string, string>(){
            {"0", "Skip Tick" },
            {"1", "Skip Draw" },
        };

        public static Dictionary<string, Dictionary<string, string>> KeyEnums = new Dictionary<string, Dictionary<string, string>>() {
            { "VR_RenderingMethod", RenderingMethodValues },
            { "VR_SyncedSequentialMethod", SyncedSequentialMethodValues },
        };
    };

    class MandatoryConfig {
        public static Dictionary<string, string> Entries = new Dictionary<string, string>() {
            { "VR_RenderingMethod", ((int)RenderingMethod.NativeStereo).ToString() },
            { "VR_SyncedSequentialMethod", ((int)SyncedSequentialMethods.SkipDraw).ToString() },
            { "VR_UncapFramerate", "true" },
            { "VR_Compatibility_SkipPostInitProperties", "false" }
        };
    };

    class GameSettingTooltips {
        public static string VR_RenderingMethod =
        "Native Stereo: The default, most performant, and best looking rendering method (when it works). Runs through the native UE stereo pipeline. Can cause rendering bugs or crashes on some games.\n" +
        "Synced Sequential: A form of AFR. Can fix many rendering bugs. It is fully synchronized with none of the usual AFR artifacts. Causes TAA/temporal effect ghosting.\n" +
        "Alternating/AFR: The most basic form of AFR with all of the usual desync/artifacts. Should generally not be used unless the other two are causing issues.";

        public static string VR_SyncedSequentialMethod =
        "Requires \"Synced Sequential\" rendering to be enabled.\n" +
        "Skip Tick: Skips the engine tick on the next frame. Usually works well but sometimes causes issues.\n" +
        "Skip Draw: Skips the viewport draw on the next frame. Works with least issues but particle effects can play slower in some cases.\n";

        public static Dictionary<string, string> Entries = new Dictionary<string, string>() {
            { "VR_RenderingMethod", VR_RenderingMethod },
            { "VR_SyncedSequentialMethod", VR_SyncedSequentialMethod },
        };
    }

    public class ValueTemplateSelector : DataTemplateSelector {
        public DataTemplate? ComboBoxTemplate { get; set; }
        public DataTemplate? TextBoxTemplate { get; set; }
        public DataTemplate? CheckboxTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) {
            var keyValuePair = (GameSettingEntry)item;
            if (ComboMapping.KeyEnums.ContainsKey(keyValuePair.Key)) {
                return ComboBoxTemplate;
            } else if (keyValuePair.Value.ToLower().Contains("true") || keyValuePair.Value.ToLower().Contains("false")) {
                return CheckboxTemplate;
            } else {
                return TextBoxTemplate;
            }
        }
    }
    #endregion


    public partial class MainWindow : Window {
        static string? backend_dir;
        // single-instance mutex to avoid multiple instances repeatedly starting/killing each other
        private static Mutex? s_instanceMutex;

        static List<string> m_whiteList = new();
        #region variables
        // process list
        private List<Process> m_processList = new List<Process>();
        // Only UE games with a custom platform window

        internal MainWindowSettings m_mainWindowSettings = new MainWindowSettings();
        private bool m_focusGameOnInjection;
        private bool m_nullifyVRPlugins;
        private DateTime m_uevrStartTime;
        private List<string> profiles;
        private string? m_lastSelectedProcessName = new string("");
        private int? m_lastSelectedProcessId = 0;
        private List<Process> m_excluded_processes = new List<Process>();
        // for temporary exclusions to prevent attempted autoinjection, e.g. protected procs
        private List<string> m_excludedNames = new List<string>();

        private Data? m_lastSharedData = null;
        private bool m_connected = false;
        private int? m_pid;
        private Process? m_connectedProc;

        private DispatcherTimer m_updateTimer = new DispatcherTimer {
            Interval = new TimeSpan(0, 0, 2)
        };

        private IConfiguration? m_currentConfig = null;
        private string? m_currentConfigPath = null;


        private ManagementEventWatcher? watcher;
        private string? m_commandLineAttachExe = null;
        private bool m_ignoreFutureVDWarnings = false;
        internal Updater _updater = new Updater();
        private string m_runtime = "openxr_loader.dll";
        private bool m_paused_injection;
        private bool m_hasMouse;
        private bool m_mouseClicked;
        private bool m_windowHasFocus;
        private bool canDragWindow;
        private bool isDragFile;
        private bool settingsMenuOpen = false;

        // so you can start to tray from a command line without necessarily setting the option for it
        private bool m_startMinimizedOnce;
        // system tray icon 
        // MainWindow initializes and updates the notification icon
        // the notifyIcon (really its message handler window) can change MainWindow state
        // it also handles the context menu window
        // notifyIcon is solely responsible for managing the context menu
        // MainWindow is solely responsible for managing the notifyIcon
        private NotifyIcon _notifyIcon;
        // ensure only one modal dialog is shown at a time to avoid overlapping dialogs at startup
        private static readonly object m_dialogLock = new object();
        // When set true the startup dialog prompts will be suppressed once (used when restarting elevated)
        private bool m_skipDialogsOnStartup = false;

        #endregion
        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;
        public MainWindow() {
            m_uevrStartTime = DateTime.Now;
            try {
                // enforce single instance early to avoid restart/shutdown loops caused by multiple processes
                try {
                    bool createdNew = false;
                    s_instanceMutex = new Mutex(true, "Global\\UEVRInjector_SingleInstance_Mutex_v1", out createdNew);
                    if (!createdNew) {
                        // Another instance is already running; exit this one to avoid fight between instances.
                        Application.Current?.Shutdown();
                        return;
                    }
                } catch { }

                // this is just to allow running the injector from a random folder, e.g. from backend build folder and using that version
                // either way we'll load from current directory but we only change to unrealvrmod/uevr if no local override exists
                var localDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.GetFileName("UEVRBackend"));
                var uevrDir = Path.Combine(GetGlobalDir(), "UEVR");

                if (!File.Exists(localDll) && Directory.GetCurrentDirectory() != uevrDir) {
                    Directory.SetCurrentDirectory(uevrDir);

                    backend_dir = uevrDir;
                } else {
                    backend_dir = Directory.GetCurrentDirectory().ToString();

                }
            } catch { }
            try {
                // Use the actual current process executable name (without extension) so we
                // reliably find other running instances regardless of publish mode (single-file, etc).
                var currentExeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "UEVRInjector");
                foreach (var proc in Process.GetProcessesByName(currentExeName)) {
                    if (proc.Id != Environment.ProcessId) {
                        TerminateProcessWMI(proc.Id);
                    }
                }
            } catch { }

            this.DataContext = m_mainWindowSettings;
            InitializeComponent();
            if (_notifyIcon is null) _notifyIcon = new NotifyIcon();
            _notifyIcon.InitializeTrayIcon(this);

            this.Loaded += async (_, __) => {

                // Grab the command-line arguments
                string [] args = Environment.GetCommandLineArgs();

                // Parse and handle arguments
                foreach (string arg in args) {
                    if (arg.StartsWith("--attach=")) {
                        m_commandLineAttachExe = arg.Split('=') [1];
                    }
                    if (arg.StartsWith("--console")) {
                        var handle = GetConsoleWindow();
                        ShowWindow(handle, 5);
                    }
                    // allow launching to tray without modifying the setting
                    if (arg.StartsWith("--minimized")) {
                        m_startMinimizedOnce = true;
                    }
                    if (arg.StartsWith("--elevated")) {
                        m_skipDialogsOnStartup = true;
                    }
                    // allow injection and simultaneous launch by dragging a game onto a shortcut to UEVR or passing full path as an arg
                    if (File.Exists(arg) && Path.GetExtension(arg) == ".exe") {
                        m_commandLineAttachExe = Path.GetFileName(arg);
                        if (!IsProcessRunning(m_commandLineAttachExe, out Process? p)) {
                            LaunchProcessUnelevated(arg);
                        } else if (p is not null) {
                            _ = InjectIntoProcess((uint?)p.Id, m_commandLineAttachExe);
                        }
                    }

                }
                // remove junk profile folders, won't remove anything with files in it
                // aside from obvious reasons this is so we can early inject into games with profiles 
                GameConfig.Cleanup();

                // allow the option to never even see the window
                if (m_mainWindowSettings.OpenToTray.Equals(true) || m_startMinimizedOnce) {
                    this.Visibility = Visibility.Hidden;
                    this.ShowInTaskbar = false;
                }







            profiles = new List<string>();
            var profdirs = Directory.GetDirectories(GetGlobalDir());
            foreach (var prof in profdirs) {
                profiles.Add(prof.Split("UnrealVRMod").Last());
            }
            if (m_virtualDesktopChecked || m_virtualDesktopWarned || CheckSetting("IgnoreFutureVDWarnings")) {
                // this will also introduce the feature the first time and ask permission to always run
                if (AllowAutoInjectFeature())
                    FindAndInject();
            }
           


            /*          
                        this feature uses wmi to assist in early injection and supplements our periodic scans with precise process creation events
                        allows for automated injection to cover all cases that up until now have required performance intensive injectors 
                        that can only load when given a specific exe to scan for on the commandline and essentially just bruteforce iterate processes,
                        require a custom dll to load by proxy, or globally hook CreateProcess like Windhawk does. Except this uses no hooks and the
                        only dependency is the Microsoft System Management dll required for WMI which is already useful enough to include 
                        given its ability to retrieve command lines from already running processes and get executable paths in edge cases where 
                        limited access tokens are inadequate to open a process handle.

                        This actually typically gives us enough time we could load into nearly every Unreal game, even when kernel protection is involved
                        and even for first time processes we can quickly run some checks to identify UE and load in long before a window is created
                        That seems excessive however so this will only occur if the game is on the commandline or a profile exists - 
                        which puts the responsibility on the user. People who have been using alternative injectors and have profiles can migrate
                        and anyone else can either import a profile or launch with the command line, either way its up to the user to decide    
                        and any misuse is on them. To be clear, UEVR does not bypass anything and does not have any specific code 
                        either in the frontend or backend designed to defeat security mechanisms. Its just a really good injector
                        That being said the user mode startup options for steam and epic will accidentally disable some anticheats,
                        this is the fault of the game developers for not launching their process with an elevated cmd,
                        or checking that the service actually started and connected to the game. Games that do ask for elevation will work normally.
                        
            */
            if (IsAdministrator() && CheckSetting("ProcessStartTraceInjection")) {

                foreach (var proc in m_mainWindowSettings.WhiteListedGames) {
                    if (!m_whiteList.Contains(Path.GetFileName(proc)!))
                        m_whiteList.Add(proc!);
                }
                foreach (var prof in profiles) {
                    if (!m_mainWindowSettings.BlackListedGames.Contains(Path.GetFileName(prof))
                        && IsValidProfile(prof)
                        && !m_whiteList.Contains(Path.GetFileName(prof)))
                        m_whiteList.Add(prof);
                }
                if (m_commandLineAttachExe is not null && !m_whiteList.Contains(m_commandLineAttachExe))
                    m_whiteList.Add(m_commandLineAttachExe);

                watcher = new ManagementEventWatcher(
                    "SELECT * FROM Win32_ProcessStartTrace");
                watcher.EventArrived += OnStartTrace;
                watcher.Start();
            }
            };
        }

        internal void UpdateSetting(string propertyName, object value) {
            m_mainWindowSettings [propertyName] = value;
            try {
                m_mainWindowSettings.Save();
            } catch { }
        }


        public bool CheckSetting(string propertyName) {
            return m_mainWindowSettings [propertyName].Equals(true);
        }

        internal void ResetSettings() {
            m_mainWindowSettings.Reset();
        }

        #region update


        internal async Task<bool> AllowUpdateOnce() {
            if (CheckSetting("AutomaticNightlyUpdates")) return true;
            else if (!CheckSetting("IntroducedUpdateFeature")) {
                if (AllowAutoUpdateFeature()) {
                    return true;
                }
            }
            string message = "A new nightly version is available\n" +
                "These releases often bring new features and fixes needed for certain games and profiles.\n" +
                "Would you like to download the update?";
            var dialog = new YesNoDialog("Download update", message);
            lock (m_dialogLock) {
                dialog.Owner = this;
                dialog.Topmost = true;
                dialog.BringIntoView();
                dialog.Focus();
                dialog.Activate();
                dialog.ShowDialog();
            }
            var wants_update = dialog.DialogResultYes;
            return wants_update;
        }

        private bool AllowAutoUpdateFeature() {
            string message = "UEVR Injector is capable of automatically updating the backend.\n" +
            "These releases often bring new features and fixes and are highly recommended.\n" +
            "Would you like to enable automatic nightly updates?\n" +
            "If you select no then you will be prompted for permission each time an update is available.";


            var backend = Path.Combine(GetGlobalDir(), "UEVR", "UEVRBackend.dll");
            if (!File.Exists(backend)) {
                var firstInstallDialog = new YesNoDialog("Automatic Update Feature", message);
            }
            if (CheckSetting("IntroducedUpdateFeature")) return CheckSetting("AutomaticNightlyUpdates");

            var dialog = new YesNoDialog("Automatic Update Feature", message);
            lock (m_dialogLock) {
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            UpdateSetting("IntroducedUpdateFeature", true);
            var wants_updates = dialog.DialogResultYes;
            if (wants_updates) {
                UpdateSetting("AutomaticNightlyUpdates", true);
                return true;
            }
            return false;
        }


        #endregion

        #region autoinject
        // if a new game without a profile is detected and the feature is disabled we'll open a popup
        private bool AutoInjectNewGame(string game) {
            if (CheckSetting("AutoInjectNewGames")) return true;
            string message = $"Detected Unreal Engine game without a profile\nInject UEVR into {game}?";
            var dialog = new YesNoDialog("Inject", message);
            lock (m_dialogLock) {
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            var wants_inject = dialog.DialogResultYes;
            return wants_inject;
        }
        private bool AllowAutoInjectFeature() {
            if (CheckSetting("IntroducedAutoInjectFeature")) return CheckSetting("AutomaticInjection");
            string message = "UEVR Injector is now capable of fully automatic injection!\n" +
                "Only valid Unreal Engine games with existing profiles will be injected into automatically.\n" +
                "If you select no then you will be prompted when a new game is detected.\n";
            var dialog = new YesNoDialog("Automatic injection feature", message);
           lock (m_dialogLock) {
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            var wants_autoinject = dialog.DialogResultYes;
            UpdateSetting("IntroducedAutoInjectFeature", true);

            if (wants_autoinject) {
                UpdateSetting("AutomaticInjection", true);
                return true;
            }
            return false;
        }

        public async void FindAndInject() {
            // every single unreal engine program has an UnrealWindow class window
            // so we just call FindWindow which scans all windows and then grab the process
            // simple, easy, ensures the game has already made a window so we won't inject too early
            var uepid = FindUnrealWindow();
            if (uepid != 0) {
                // this is a temporary list we fill if we fail to inject so that we don't try again
                if (m_excluded_processes.Count > 0) {
                    foreach (var proc in m_excluded_processes) {
                        if (proc.Id == (int)uepid) {
                            return;
                        }
                    }
                }
                try {
                    var proc = Process.GetProcessById((int)uepid);
                    if (!CanAutoInject(proc)) {
                        return;
                    }
                    if (!IsAdministrator() && IsProcessElevated(proc)) {
                        try {
                            m_connectionStatus.Text = "Process is elevated, restart as admin to continue.";
                        } catch { }
                        m_nNotificationsGroupBox.Visibility = Visibility.Visible;
                        m_restartAsAdminButton.Visibility = Visibility.Visible;
                        m_adminExplanation.Visibility = Visibility.Visible;
                        return;
                    }
                    if (proc is null) return;
                    var name = proc.MainModule!.FileName;
                    if (name is null) return;

                    if (!IsValidProfile(Path.GetFileNameWithoutExtension(name))) {
                        if (CheckSetting("AutoInjectNewGames")) {
                            if (!NullableContains(m_commandLineAttachExe, name))
                                return;
                        }
                    }
                    await InjectIntoProcess(uepid, name);
                } catch { }
            }
        }

        public void PauseAutoInject() {
            if (!m_connected) {
                m_paused_injection = true;
                _notifyIcon.ModifyTrayIcon("_paused");
            }
        }

        public void UnPauseAutoInject() {
            m_paused_injection = false;
            if (!m_connected) {
                _notifyIcon.ModifyTrayIcon("default");
            }
        }

        // called by the notifyIcon
        // notifyIcon holds refs to mainwindow and context menu
        public bool IsInjectionPaused() {
            return m_paused_injection;
        }
        #endregion
        #region monitoring
        // Lua errors can get stuck on the wrong window
        public static async Task FindLuaMessage() {
            var window = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "LuaLoader Message");
            if (window != IntPtr.Zero) {
                Console.WriteLine("Found Lua Loader");
                Console.WriteLine(window.ToString());
                var parent = GetParent(window);
                _ = GetWindowThreadProcessId(window, out uint windowpid);
                Process uep = Process.GetProcessById((int)windowpid);
                // set window parent to null to parent to desktop
                SetParent(window, IntPtr.Zero);
                BringWindowToTop(window);
                SwitchToThisWindow(window, true);
            }
        }
        public bool IsConnected() {
            return m_connected;
        }
        private async Task InjectIntoProcess(uint? pid, string? name) {
            Directory.SetCurrentDirectory(backend_dir!);
            var p = Process.GetProcessById((int)pid!);
            Injector.InjectDllAsync((uint)pid, "UEVRBackend.dll");
            try {
                if (CheckProcessForModule(p, "UEVRBackend.dll", out _, out _)) {
                    if (!Injector.InjectDll((uint)pid, m_runtime)) {
                        // Idea here is to force UEVRbackend to load the runtime from global plugins folder and delete 
                        // the runtime copy after game closes so that it doesnt prevent switching to a different runtime
                        // probably better to just make a new PR to fix UEVR paths for loading runtimes
                        MessageBox.Show("UEVR was succesfully injected but the VR runtime could not be loaded.\nWe will try to load the runtime as a plugin.\nYou will need to open the UEVR menu and click reload plugins and then try reinitialize runtime.");
                        File.Copy(Path.Combine(GetGlobalDir(), "UEVR", m_runtime), Path.Combine(GetGlobalDir(), "UEVR", "Plugins", m_runtime));
                        await CleanupScheduler.DeleteWhenUnlockedAsync(Path.Combine(GetGlobalDir(), "UEVR", "Plugins", m_runtime), new CancellationToken(), p);
                    }
                    Injector.InjectDllAsync((uint)pid, "LuaVR");
                    if (name is null) name = p.ProcessName;
                    InitializeConfig(Path.GetFileNameWithoutExtension(name));
                    if (m_nullifyVRPlugins) {
                        try {
                            var proc = Process.GetProcessById((int)pid);
                            var enginePlugs = Detect_Engine_Plugins(proc);
                            if (enginePlugs.Length != 0) {
                                _ = DeleteEnginePluginsOnExit(proc);
                                IntPtr nullifierBase;
                                if (Injector.InjectDll((uint)pid, "UEVRPluginNullifier.dll", out nullifierBase) && nullifierBase.ToInt64() > 0) {
                                    Injector.CallFunctionNoArgs((uint)pid, "UEVRPluginNullifier.dll", nullifierBase, "nullify", true);
                                } else {
                                    MessageBox.Show("Failed to inject plugin nullifier.");
                                }
                            }
                        } catch (Exception ex) { Debug.WriteLine($"InjectIntoProcess(nullify) error: {ex}"); }
                    }
                    try {
                        m_pid = (int?)pid;
                        if (_notifyIcon.GetIconType() != "_connected") {
                            m_connected = true;
                            _notifyIcon.ModifyTrayIcon("_connected");
                            _notifyIcon.ModifyToolTip(Process.GetProcessById((int)pid)!.MainModule!.FileName!.ToString());
                            _notifyIcon.ShowConnectionOptions();
                       
                        }
                    } catch {}
                } else {
                    if (IsAdministrator() && !CheckSetting("ProcessStartTraceInjection")) {
                        m_excluded_processes.Add(Process.GetProcessById((int)pid));
                    } else {
                        m_connectionStatus.Text = "Failed to obtain handle to process.";
                        m_nNotificationsGroupBox.Visibility = Visibility.Visible;
                        m_restartAsAdminButton.Visibility = Visibility.Visible;
                        m_adminExplanation.Visibility = Visibility.Visible;
                    }
                }
            } catch { }
        }


        private async Task DeleteEnginePluginsOnExit(Process p) {
            if (!IsProcessRunning(p)) return;
            if (!GetExecutablePath(p, out string? path)) return;
            using var cts = new CancellationTokenSource();
            try {
                if (IsProcessRunning(p))
                    await p.WaitForExitAsync(cts.Token);
                Task cleanup = DisableEnginePlugins(path);
                await Task.Run(async () => cleanup);
            } catch (OperationCanceledException) {
            }
        }

        private static string [] Detect_Engine_Plugins(Process p) {
            string [] outDlls = new string [] { };
            var vrDlls = new string? [] { "openvr_api", "oculusxr", "openxr_loader" };
            foreach (ProcessModule m in p.Modules) {
                var dll = m.FileName;
                if (vrDlls.Any(f => NullableContains(dll, f))) {
                    if (NullableContains(dll, GetGlobalDir())) {
                        continue;
                    } else {
                        outDlls.Append(dll);
                    }
                }
            }
            return outDlls;
        }

        private static string? GetGameRootPath(string? path) {
            if (path is null) return null;
            try {
                var win64 = Path.GetDirectoryName(path);
                var binaries = Path.GetDirectoryName(win64);
                var gamename = Path.GetDirectoryName(binaries);
                var gameroot = Path.GetDirectoryName(gamename);
                return gameroot;
            } catch { }
            return null;
        }

        private async Task DisableEnginePlugins(string? path) {
            try {
                var vrDlls = new string [] { "openvr_api", "oculusxr", "openxr_loader", "sl.interposer", "nvngx_dlssg", "sl.dlss_g" };
                List<string> enginePlugins = new List<string>();
                var gameroot = GetGameRootPath(path);
                if (gameroot is null) return;
                var dlls = Directory.GetFileSystemEntries(gameroot, "*.dll", SearchOption.AllDirectories);
                foreach (var dll in dlls) {
                    if (vrDlls.Any(f => NullableContains(dll, f))) {
                        enginePlugins.Add(dll);
                    }
                }
                if (enginePlugins.Count != 0) {
                    foreach (var plug in enginePlugins) {
                        File.Move(plug, Path.ChangeExtension(plug, ".bak"));
                    }
                }
            } catch { }
        }



        static async void OnStartTrace(object sender, EventArrivedEventArgs e) {
            uint pid = (uint)e.NewEvent ["ProcessID"];
            string name = (string)e.NewEvent ["ProcessName"];
            string baseName = name.Replace(".exe", "").ToLower();
            if (backend_dir is null) backend_dir = Path.Combine(GetGlobalDir(), "UEVR");
            Directory.SetCurrentDirectory(backend_dir);
            if (!IsProfile(baseName) && !Contains(m_whiteList, baseName))
                return;
            var consent = Process.GetProcessesByName("consent.exe");
            if (consent.Length != 0) {
                try {
                    foreach (var proc in Process.GetProcessesByName(name)) {
                        if (proc.Id == (int)pid) continue;
                        new Thread(() => {
                            Directory.SetCurrentDirectory(backend_dir);
                            Injector.InjectDllAsync((uint)proc.Id, "UEVRBackend.dll");
                        }).Start();
                    }
                } catch { }
                return;
            }
            // Delay to filter out fake/decoy launches  
            Process? p = null;
            try { p = Process.GetProcessById((int)pid); } catch { return; } 
            await Task.Delay(500);
            if (p.HasExited) return;
            await Task.Delay(500);
            if (p.WorkingSet64 < 10_000_000) return; // tiny decoy process
            await Task.Delay(500);
            new Thread(() => {
                Directory.SetCurrentDirectory(backend_dir);
                Injector.InjectDllAsync((uint)pid, "UEVRBackend.dll");
            }).Start();
            Console.WriteLine($"\n[TRACE] PID={pid} NAME={name} TIME={DateTime.Now:HH:mm:ss.fff}");
        }

            private void Update_InjectStatus(Process? p) {
            try {
                if (m_paused_injection && !m_connected) {
                    if (_notifyIcon.GetIconType() != "_paused")
                        _notifyIcon.ModifyTrayIcon("_paused");
                    _notifyIcon.ModifyToolTip("Injection Paused");
                    return;
                }


                if (_notifyIcon.GetIconType() != "_connected" && p is not null) {
                    _notifyIcon.ModifyTrayIcon("_connected");
                    _notifyIcon.ModifyToolTip(p.ProcessName);
                    _notifyIcon.ShowConnectionOptions();
                    m_injectButton.Content = "Terminate Connected Process";
                    m_lastSelectedProcessId = p.Id;
                    m_lastSelectedProcessName = p.ProcessName;
                    return;
                }

                if (p is null || p!.HasExited) {
                    p = Process.GetProcessById((int)m_pid!);

                    if (p is not null) {
                        return;
                    } else {
                        p = Process.GetProcessById((int)m_lastSelectedProcessId!);
                    }
                    if (p is not null) {
                        return;
                    }

                    m_connected = false;
                    m_pid = null;
                    _notifyIcon.ResetTrayIcon();
                    _notifyIcon.ModifyToolTip();
                    m_lastSelectedProcessId = null;
                    m_lastSelectedProcessName = null;

                    return;
                }
            } catch { }

            DateTime now = DateTime.Now;
            TimeSpan oneSecond = TimeSpan.FromSeconds(1);
            FindAndInject();

            if (Visibility == Visibility.Visible && !m_connected) {
                FillProcessList();

                try {
                    var verifyProcess = m_connectedProc is not null ? m_connectedProc :
                                                   m_pid is not null ? Process.GetProcessById((int)m_pid) :
                                                   Process.GetProcessById((int)m_lastSelectedProcessId!);

                    if (verifyProcess == null || verifyProcess.HasExited || verifyProcess.ProcessName != m_lastSelectedProcessName) {
                        var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                        if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                            m_injectButton.Content = "Waiting for Process";
                            return;
                        }
                    }

                    m_injectButton.Content = "Inject";
                } catch (ArgumentException) {
                    var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                    if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                        m_injectButton.Content = "Waiting for Process";
                        return;
                    }

                    m_injectButton.Content = "Inject";
                }
            }
        }

        private bool CanAutoInject(Process? p) {
            var autoInjection = CheckSetting("AutomaticInjection");
            if (!autoInjection) return false;
            if (p is null) return false;
            string? name = null;
            string? uevr_profile = null;
            try {
                GetExecutablePath(p.Id, out string? executable_path);
                name = Path.GetFileNameWithoutExtension(executable_path!);
                uevr_profile = Path.Combine(GetGlobalDir(), name!);
            } catch { 
                return false; 
            }

            if (name is null || IsEpicApp(uevr_profile!)) return false;
            var hasProfile = Directory.Exists(uevr_profile);
            if (autoInjection && (hasProfile || (CheckSetting("AutoInjectNewGames")))) return true;
            // since we cleanup invalid profiles now we can usually just autoinject if a profile exists
            if (hasProfile) {
                if (File.Exists(Path.Combine(uevr_profile!, "log.txt"))) {
                    if (!ValidateLog(uevr_profile))  // last start failed to initialize runtime so lets see if we should autoinject
                    {
                        // excluded process list is a temporary once per program run list
                        // mainly for protected processes or games that crash instantly
                        // so we don't try to inject into those after the first failure
                        // otherwise if there are uobjecthook items, plugins, or scripts
                        // then we'll assume last failed initialization was a random error and try again
                        // if that fails we'll be on the excluded process list 
                        if (m_excluded_processes.Contains(p) ||
                            CheckSubdir(Path.Combine(uevr_profile!, "uobjecthook")) +
                            CheckSubdir(Path.Combine(uevr_profile!, "scripts")) +
                            CheckSubdir(Path.Combine(uevr_profile!, "data")) +
                            CheckSubdir(Path.Combine(uevr_profile!, "plugins")) == 0) {
                            // still add the process to the list so manual injection can be attempted
                            // this is okay since we cleanup the profiles before ever hitting this point
                            if (!m_processList.Contains(p)) {
                                m_processList.Add(p);
                            }
                            return false;
                        } else {
                            return true;
                        }
                    }
                }
            }
            if (m_excluded_processes.Count > 0) {
                foreach (var proc in m_excluded_processes) {
                    if (proc == p) {
                        return false;
                    }
                }
            }
            return AutoInjectNewGame(p.MainWindowTitle);
        }


        private static bool IsInjectableProcess(Process p) {
            if (GetExecutablePath(p.Id, out string? executable_path)) {
                return  !IsEpicApp(p) && 
                            (CheckProcessForUnrealWindow(p) || 
                            m_whiteList.Contains(Path.GetFileNameWithoutExtension(executable_path!)));
            }
            return false;
        }

        private bool AnyInjectableProcesses(Process [] processes) {
            if (m_connected) return false;
            var uepid = FindUnrealWindow();
            if (uepid == 0) return false;
            foreach (var proc in processes) {
                if (IsInjectableProcess(proc)) return true;
            }
            return false;
        }

        private void Hide_ConnectionOptions() {
            m_openGameDirectoryBtn.Visibility = Visibility.Collapsed;
        }

        private void Show_ConnectionOptions() {
            m_openGameDirectoryBtn.Visibility = Visibility.Visible;
        }

        private static bool TryGetProcess(int? pidNullable, out Process? process) {
            process = null;
            if (pidNullable == null) return false;
            int pid = pidNullable.Value;
            try {
                var p = Process.GetProcessById(pid);
                if (p == null || p.HasExited) {
                    process = null;
                    return false;
                }
                process = p;
                return true;
            } catch {
                process = null;
                return false;
            }
        }

        // not even actually using this but it may be useful for addressing engine plugin issues
        private static bool CheckProcessForModule(uint pid, string moduleName, out nint moduleHandle, out string modulePath) {
            moduleHandle = 0;
            modulePath = string.Empty;
            try {
                var p = Process.GetProcessById((int)pid);
                return CheckProcessForModule(p, moduleName, out moduleHandle, out modulePath);
            } catch {
                return false;
            }
        }

        private static bool CheckProcessForModule(Process process, string moduleName, out nint moduleHandle, out string modulePath) {
            moduleHandle = 0;
            modulePath = string.Empty;
            if (process == null) return false;
            try {
                foreach (ProcessModule m in process.Modules) {
                    if (m.FileName != null && (m.FileName.EndsWith(moduleName, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(m.FileName).Equals(moduleName, StringComparison.OrdinalIgnoreCase))) {
                        moduleHandle = (nint)m.BaseAddress;
                        modulePath = m.FileName;
                        return true;
                    }
                }
            } catch {
            }
            return false;
        }

        private void ResetConnectionStatus() {
            m_connected = false;
            if (_notifyIcon.GetIconType() == "_connected") _notifyIcon.ModifyTrayIcon(m_paused_injection ? "_paused" : "default");
            m_connectionStatus.Text = UEVRConnectionStatus.NoInstanceDetected;
            Hide_ConnectionOptions();

            if (CheckSetting("AutomaticInjection"))
                FindAndInject();
        }

        private async Task Update_InjectorConnectionStatus() {

            try {
                var data = GetData();
                int? pid = data is not null ? data!.Value.pid : 0;

                // Typically we should get a sharedmemory connection established even if injecting into a protected process
                // but if for one reason or another we have a value assigned to m_pid during injection and can't get shared mem connected
                // we can still go ahead and update our connection status
                // in this case we will attempt to use a limited info token to get the path (works on elevated procs while unelevated)
                // failing that we use wmi (works everywhere)
                if (!NullableEquals(pid, m_pid)) {
                    if (!TryGetProcess(m_pid, out Process? proc)) {
                        m_pid = null;
                        return;
                    } else if (proc is not null) {
                        Update_InjectStatus(proc);
                        // Start monitoring the process exit without blocking the UI thread.
                        if (CheckProcessForModule(proc, "UEVRBackend.dll", out _, out _)) {
                            _ = MonitorProcessExitAsync(proc);
                        }
                        return;
                    }
                }
            } catch { }
        }

        // Monitors a process for exit and resets connection status when it exits.
        private async Task MonitorProcessExitAsync(Process proc) {
            try {
                using var cts = new CancellationTokenSource();
                await proc.WaitForExitAsync(cts.Token);
                // Ensure this runs on the UI thread when modifying UI state.
                await Dispatcher.InvokeAsync(() => ResetConnectionStatus());
            } catch { }
        }
        #endregion

        #region window
        // public to allow access from other windows
        public async Task Update() {
            try {

                if (!m_connected && !m_paused_injection) {
                    FindAndInject();
                }
                await MainWindow_Update();


                // Once per hour check to see the last nightly update time
                if (DateTime.Now.Minute == 0 && DateTime.Now.Second <= 3) {
                    try {
                        // honestly it wouldn't really hurt to just run the update check
                        // but its goofy to be checking every hour for something that updates at most every few days
                        // nevertheless this allows users who never shut off their pc to just leave this running forever
                        // and avoids having to mess with schedulers or any nonsense like that
                        var last = m_mainWindowSettings.LastUpdated;
                        var freq = m_mainWindowSettings.UpdateCheckFrequency;
                        if (last != default(DateTime) && freq != default(TimeSpan)) {
                            try {
                                // default update period is 12 hours
                                if (DateTime.Now - last >= freq) {
                                    try {
                                        if (_updater.IsUpdateAvailable(_updater.GetCurrentRevision(), await _updater.GetReleasesAsync(), out GitAPI.GitHubResponseObject? Update)) {
                                        if (Update is not null ) {
                                                await _updater.DownloadAndExtractAsync(Update);
                                            }
                                        
                                        }
                                    } catch (Exception ex) { Debug.WriteLine($"Periodic CheckForNightlyUpdates failed: {ex}"); }
                                }
                            } catch { }
                        }
                    } catch { }

                }
            } catch (Exception ex) {
                Debug.WriteLine($"Update error: {ex.Message}");
            }
        }

        private async Task MainWindow_Update() {

            if (m_connected) {
                Task checkStatus = Update_InjectorConnectionStatus();
                await checkStatus.WaitAsync(new CancellationToken());
                if (TryGetProcess((int?)m_pid, out Process? p)) {
                    if (p is not null) {
                        if (!CheckProcessForModule((uint)m_pid!, "UEVRBackend.dll", out _, out _)) {
                            await InjectIntoProcess((uint)m_pid, null);
                        }
                        var cts = new CancellationToken();
                        await p.WaitForExitAsync(cts);
                        ResetConnectionStatus();
                    }
                }

                if (m_connected) {
                    //  Update_InjectStatus(p);
                    await FindLuaMessage();
                    Thread.Sleep(1000);
                } else {
                    FindAndInject();
                }
            }
            if (m_excluded_processes is not null)
                if (m_excluded_processes.Count > 0) {
                if (!IsAdministrator()) {
                    m_nNotificationsGroupBox.Visibility = Visibility.Visible;
                    m_restartAsAdminButton.Visibility = Visibility.Visible;
                    m_adminExplanation.Visibility = Visibility.Visible;
                }
            }
            if (!CheckSetting("IgnoreFutureVDWarnings")) {
                m_virtualDesktopChecked = true;
                // If we were relaunched elevated, suppress the virtual desktop warning once to avoid dialog storms
                if (m_skipDialogsOnStartup) {
                    m_skipDialogsOnStartup = false;
                } else {
                    Check_VirtualDesktop();
                }
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            m_mainWindowSettings.Save();
            try { _notifyIcon.RemoveTrayIcon(); } catch { }
            Application.Current.Shutdown();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
            // does not hide taskbar icon, can be opened from taskbar or by double clicking notify icon
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) {
            try {
                if (m_mainWindowSettings.CloseFromWindow) {
                    // Close application fully if old behavior is preferred
                    _notifyIcon.RemoveTrayIcon();
                    Application.Current.Shutdown();
                } else {
                    this.Visibility = Visibility.Hidden;
                    this.ShowInTaskbar = false;
                }
            } catch {
                this.Visibility = Visibility.Hidden;
                this.ShowInTaskbar = false;
            }
        }


        #endregion
        #region application
        public static bool IsAdministrator() {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            // Check for updates after window has loaded. Suppress on elevated relaunch to avoid prompt storms.
            try {
                if (!m_skipDialogsOnStartup) {
                    if (_updater.IsUpdateAvailable(_updater.GetCurrentRevision(), await _updater.GetReleasesAsync(), out GitAPI.GitHubResponseObject? Update)) {
                        if (Update is not null) {
                            await _updater.DownloadAndExtractAsync(Update);
                        }

                    }
                } else {
                    m_skipDialogsOnStartup = false;
                }
            } catch (Exception ex) {
                Debug.WriteLine($"CheckForNightlyUpdates failed: {ex}");
            }

            if (Visibility == Visibility.Visible) {
                FillProcessList();
                m_openvrRadio.IsChecked = m_mainWindowSettings.OpenVRRadio;
                m_openxrRadio.IsChecked = m_mainWindowSettings.OpenXRRadio;
                m_nullifyVRPlugins = m_mainWindowSettings.NullifyVRPlugins;
                m_ignoreFutureVDWarnings = m_mainWindowSettings.IgnoreFutureVDWarnings;
                m_focusGameOnInjectionCheckbox.IsChecked = m_mainWindowSettings.FocusGameOnInjection;
            }
            m_updateTimer.Tick += async (sender, e) => await Dispatcher.InvokeAsync(async () => await MainWindow_Update());
            m_updateTimer.Start();
        }

        private static bool IsExecutableRunning(string executableName) {
            return Process.GetProcesses().Any(p => p.ProcessName.Equals(executableName, StringComparison.OrdinalIgnoreCase));
        }

        private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e) {
            RestartAsAdmin();
        }

        private void RestartAsAdmin() {
            // Determine the current executable path in a way that works for
            // single-file publishes and normal runs.
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) {
                try {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                } catch { exePath = null; }
            }
            if (string.IsNullOrWhiteSpace(exePath)) return;

            // Create a new process with administrator privileges. Pass a marker
            // argument so the relaunched instance can detect it's the elevated one.
            var processInfo = new ProcessStartInfo {
                FileName = exePath,
                Arguments = "--elevated",
                Verb = "runas",
                UseShellExecute = true,
            };

            try {
                // Attempt to start the process
                Process.Start(processInfo);
            } catch (Win32Exception ex) {
                // Handle the case when the user cancels the UAC prompt or there's an error
                MessageBox.Show($"Error: {ex.Message}\n\nThe application will continue running without administrator privileges.", "Failed to Restart as Admin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Close the current application instance. The relaunched elevated
            // instance receives --elevated to avoid any accidental restart loops.
            Application.Current.Shutdown();
        }


        #endregion

        #region shortcuts
        public static string GetGlobalDir() {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod");
            if (!Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
                // Create the UEVR folder as well on first check
                // This is one of the first calls on startup so any new user will have the directory created
                // the updater will also act as an installer in that case
                Directory.CreateDirectory(Path.Combine(directory, "UEVR"));
            }
            return directory;
        }

        public static string GlobalPlugins() {
            string directory = Path.Combine(GetGlobalDir(), "UEVR", "Plugins");
            return directory;
        }

        public static string GlobalScripts() {
            return Path.Combine(GetGlobalDir(), "Scripts");
        }

        public static string GetGameConfigDir(string gameName) {
            string directory = GetGlobalDir() + "\\" + gameName;

            if (!Directory.Exists(directory) && !IsEpicApp(gameName)) {
                Directory.CreateDirectory(directory);
            }
            return directory;
        }

        public static string GamePlugins(string gameName) {
            return Path.Combine(GetGameConfigDir(gameName), "Plugins");
        }

        public static string GameScripts(string gameName) {
            return Path.Combine(GetGameConfigDir(gameName), "Scripts");
        }

        public static void NavigateToDirectory(string directory) {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
            Process.Start(explorerPath, "\"" + directory + "\"");
        }

        internal void OpenGlobalDir() {
            string directory = GetGlobalDir();
            NavigateToDirectory(directory);
        }

        internal void OpenProfileDir() {
            string directory = GetGlobalDir();
            if (m_connected && m_pid is not null) {
                if (GetExecutablePath((int)m_pid, out string path)) {
                    directory = Path.Combine(directory, Path.GetFileNameWithoutExtension(path)!);
                }
            }
            NavigateToDirectory(directory);
        }

        private void OpenProfileDir_Clicked(object sender, RoutedEventArgs e) {
            OpenProfileDir();
        }

        internal void OpenGameDir() {
            if (m_pid is not null) {
                try {
                    if (GetExecutablePath((int)m_pid, out string? path)) {
                        NavigateToDirectory(Directory.GetParent(path!)!.ToString());
                        return;
                    }

                } catch { }
            }
            if (m_lastSharedData is null) {
                return;
            }

            var directory = m_lastSharedData?.path != null ? Path.GetDirectoryName(m_lastSharedData?.path) : null;
            if (directory == null) {
                return;
            }

            NavigateToDirectory(directory);
        }

        private void OpenGameDir_Clicked(object sender, RoutedEventArgs e) {
            OpenGameDir();
        }

        private void OpenGlobalDir_Clicked(object sender, RoutedEventArgs e) {
            OpenGlobalDir();
        }

        private void ExportConfig_Clicked(object sender, RoutedEventArgs e) {

            var exportedConfigsDir = GetGlobalDir() + "\\ExportedConfigs";

            if (!m_connected) {
                var openFileDialog = new OpenFileDialog {
                    DefaultExt = ".txt",
                    Filter = "Config files (config.txt)|config.txt",
                    InitialDirectory = GetGlobalDir()
                };

                bool? result = openFileDialog.ShowDialog();
                if (result == true) {
                    var _dir = Path.GetDirectoryName(openFileDialog.FileName);
                    if (_dir is not null) {
                        CreateZipFromDirectory(_dir, exportedConfigsDir + "\\" + _dir.Substring(GetGlobalDir().Length) + ".zip");
                        NavigateToDirectory(exportedConfigsDir);
                    }
                }
                return;
            }

            if (m_lastSharedData == null) {
                MessageBox.Show("No game connection detected.");
                return;
            }

            var dir = GetGameConfigDir(m_lastSelectedProcessName!);
            if (dir == null) {
                return;
            }

            if (!Directory.Exists(dir)) {
                MessageBox.Show("Directory does not exist.");
                return;
            }


            if (!Directory.Exists(exportedConfigsDir)) {
                Directory.CreateDirectory(exportedConfigsDir);
            }

            GameConfig.CreateZipFromDirectory(dir, exportedConfigsDir + "\\" + m_lastSelectedProcessName + ".zip");
            NavigateToDirectory(exportedConfigsDir);
        }
        public void MakeProfileFromE(object sender, RoutedEventArgs e) {

            var exportedConfigsDir = GetGlobalDir() + "\\ExportedConfigs";

            if (!m_connected) {
                var openFileDialog = new OpenFileDialog {
                    DefaultExt = ".txt",
                    Filter = "Config files (config.txt)|config.txt",
                    InitialDirectory = GetGlobalDir()
                };

                bool? result = openFileDialog.ShowDialog();
                if (result == true) {
                    var _dir = Path.GetDirectoryName(openFileDialog.FileName);
                    GameConfig.CreateZipFromDirectory(_dir, exportedConfigsDir + "\\" + _dir.Substring(GetGlobalDir().Length) + ".zip");
                    NavigateToDirectory(exportedConfigsDir);
                }
                return;
            }

            if (m_lastSharedData == null) {
                MessageBox.Show("No game connection detected.");
                return;
            }

            var dir = GetGameConfigDir(m_lastSelectedProcessName!);
            if (dir == null) {
                return;
            }

            if (!Directory.Exists(dir)) {
                MessageBox.Show("Directory does not exist.");
                return;
            }


            if (!Directory.Exists(exportedConfigsDir)) {
                Directory.CreateDirectory(exportedConfigsDir);
            }

            GameConfig.CreateZipFromDirectory(dir, exportedConfigsDir + "\\" + m_lastSelectedProcessName + ".zip");
            NavigateToDirectory(exportedConfigsDir);
        }

        private void ImportConfig_Clicked(object sender, RoutedEventArgs e) {
            ImportConfig_Impl(GameConfig.BrowseForImport(GetGlobalDir()));
        }

        private void ImportConfig_Impl(string? importPath) {
            isDragFile = false;
            if (importPath == null) {
                return;
            }

            var gameName = Path.GetFileNameWithoutExtension(importPath);
            if (gameName == null) {
                MessageBox.Show("Invalid filename");
                return;
            }

            var globalDir = GetGlobalDir();
            var gameGlobalDir = globalDir + "\\" + gameName;

            try {
                if (!Directory.Exists(gameGlobalDir)) {
                    Directory.CreateDirectory(gameGlobalDir);
                }

                bool wantsExtract = true;

                if (GameConfig.ZipContainsDLL(importPath)) {
                    string message = "The selected config file includes a DLL (plugin), which may execute actions on your system.\n" +
                                     "Only import configs with DLLs from trusted sources to avoid potential risks.\n" +
                                     "Do you still want to proceed with the import?";
                    var dialog = new YesNoDialog("DLL Warning", message);
                    lock (m_dialogLock) {
                        dialog.Owner = this;
                        dialog.ShowDialog();
                    }

                    wantsExtract = dialog.DialogResultYes;
                }

                if (wantsExtract) {
                    var finalGameName = GameConfig.ExtractZipToDirectory(importPath, gameGlobalDir, gameName);

                    if (finalGameName == null) {
                        MessageBox.Show("Failed to extract the ZIP file.");
                        return;
                    }

                    var finalDirectory = Path.Combine(globalDir, finalGameName);
                    NavigateToDirectory(finalDirectory);

                    RefreshCurrentConfig();


                    if (m_connected) {
                        SharedMemory.SendCommand(SharedMemory.Command.ReloadConfig);
                    }
                }
            } catch (Exception ex) {
                MessageBox.Show("An error occurred: " + ex.Message);
            }
        }

        #endregion shortcuts
        #region configs
        private bool m_virtualDesktopWarned = false;
        private bool m_virtualDesktopChecked = false;
        private void Check_VirtualDesktop() {
            if (m_virtualDesktopWarned || m_ignoreFutureVDWarnings) {
                return;
            }

            if (IsExecutableRunning("VirtualDesktop.Streamer")) {
                m_runtime = "openxr_loader.dll";
                var dialog = new VDWarnDialog();
                foreach(var window in this.OwnedWindows) {
                    if (window.GetType() == typeof(VDWarnDialog)) {
                        return;
                    }
                }
                lock (m_dialogLock) {
                    dialog.Owner = this;
                    dialog.ShowDialog();
                }

                if (dialog.DialogResultOK) {
                    if (dialog.HideFutureWarnings) {
                        m_ignoreFutureVDWarnings = true;
                        // Persist the user's preference
                        try { UpdateSetting("IgnoreFutureVDWarnings", true); } catch { }
                    }
                    // mark warned so we don't immediately show again in this session
                    m_virtualDesktopWarned = true;
                }
            }
        }


        private string m_lastDisplayedWarningProcess = "";
        private string [] m_discouragedPlugins = {
            "OpenVR",
            "OpenXR",
            "Oculus"
        };

        private string? AreVRPluginsPresent_InEngineDir(string enginePath) {
            string pluginsPath = enginePath + "\\Binaries\\ThirdParty";

            if (!Directory.Exists(pluginsPath)) {
                return null;
            }

            foreach (string discouragedPlugin in m_discouragedPlugins) {
                string pluginPath = pluginsPath + "\\" + discouragedPlugin;

                if (Directory.Exists(pluginPath)) {
                    return pluginsPath;
                }
            }

            return null;
        }

        private string? AreVRPluginsPresent(string gameDirectory) {
            try {
                var parentPath = gameDirectory;

                for (int i = 0; i < 10; ++i) {
                    parentPath = Path.GetDirectoryName(parentPath);

                    if (parentPath == null) {
                        return null;
                    }

                    if (Directory.Exists(parentPath + "\\Engine")) {
                        return AreVRPluginsPresent_InEngineDir(parentPath + "\\Engine");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }

            return null;
        }

        private void TextChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var textBox = (TextBox)sender;
                var keyValuePair = (GameSettingEntry)textBox.DataContext;

                // For some reason the TextBox.text is updated but thne keyValuePair.Value isn't at this point.
                bool changed = m_currentConfig [keyValuePair.Key] != textBox.Text || keyValuePair.Value != textBox.Text;
                var newValue = textBox.Text;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig [keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }


        private void CheckChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var checkbox = (CheckBox)sender;
                var keyValuePair = (GameSettingEntry)checkbox.DataContext;

                bool changed = m_currentConfig [keyValuePair.Key] != keyValuePair.Value;
                string newValue = keyValuePair.Value;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig [keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }


        private string IniToString(IConfiguration config) {
            string result = "";

            foreach (var kv in config.AsEnumerable()) {
                result += kv.Key + "=" + kv.Value + "\n";
            }

            return result;
        }

        private void SaveCurrentConfig() {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }
                var iniStr = IniToString(m_currentConfig);
                Debug.Print(iniStr);

                File.WriteAllText(m_currentConfigPath, iniStr);

                if (m_connected) {
                    SharedMemory.SendCommand(SharedMemory.Command.ReloadConfig);
                }
            } catch (Exception ex) {
                MessageBox.Show(ex.ToString());
            }
        }

        private void ComboChanged_Value(object sender, RoutedEventArgs e) {
            try {
                if (m_currentConfig == null || m_currentConfigPath == null) {
                    return;
                }

                var comboBox = (ComboBox)sender;
                var keyValuePair = (GameSettingEntry)comboBox.DataContext;

                bool changed = m_currentConfig [keyValuePair.Key] != keyValuePair.Value;
                var newValue = keyValuePair.Value;

                if (changed) {
                    RefreshCurrentConfig();
                }

                m_currentConfig [keyValuePair.Key] = newValue;
                RefreshConfigUI();

                if (changed) {
                    SaveCurrentConfig();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
            }
        }

        private void RefreshCurrentConfig() {
            if (m_currentConfig == null || m_currentConfigPath == null) {
                return;
            }

            InitializeConfig_FromPath(m_currentConfigPath);
        }

        private void RefreshConfigUI() {
            if (this.Visibility == Visibility.Visible) {
                if (m_currentConfig == null) {
                    if (m_openProfileDirectoryBtn.Visibility == Visibility.Visible)
                        m_openProfileDirectoryBtn.Visibility = Visibility.Collapsed;
                    return;
                }

                m_openProfileDirectoryBtn.Visibility = Visibility.Visible;
            }

            var vanillaList = m_currentConfig!.AsEnumerable().ToList();
            vanillaList.Sort((a, b) => a.Key.CompareTo(b.Key));

            List<GameSettingEntry> newList = new List<GameSettingEntry>();

            foreach (var kv in vanillaList) {
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) {
                    Dictionary<string, string> comboValues = new Dictionary<string, string>();
                    string tooltip = "";

                    if (ComboMapping.KeyEnums.ContainsKey(kv.Key)) {
                        var valueList = ComboMapping.KeyEnums [kv.Key];

                        if (valueList != null && valueList.ContainsKey(kv.Value)) {
                            comboValues = valueList;
                        }
                    }

                    if (GameSettingTooltips.Entries.ContainsKey(kv.Key)) {
                        tooltip = GameSettingTooltips.Entries [kv.Key];
                    }

                    newList.Add(new GameSettingEntry { Key = kv.Key, Value = kv.Value, ComboValues = comboValues, Tooltip = tooltip });
                }
            }
            if (this.Visibility == Visibility.Visible) {
                if (m_iniListView.ItemsSource == null) {
                    m_iniListView.ItemsSource = newList;
                } else {
                    foreach (var kv in newList) {
                        var source = (List<GameSettingEntry>)m_iniListView.ItemsSource;

                        var elements = source.FindAll(el => el.Key == kv.Key);

                        if (elements.Count() == 0) {
                            // Just set the entire list, we don't care.
                            m_iniListView.ItemsSource = newList;
                            break;
                        } else {
                            elements [0].Value = kv.Value;
                            elements [0].ComboValues = kv.ComboValues;
                            elements [0].Tooltip = kv.Tooltip;
                        }
                    }
                }

                m_iniListView.Visibility = Visibility.Visible;
            }
        }

        private void InitializeConfig_FromPath(string configPath) {
            var builder = new ConfigurationBuilder().AddIniFile(configPath, optional: true, reloadOnChange: false);

            m_currentConfig = builder.Build();
            m_currentConfigPath = configPath;

            foreach (var entry in MandatoryConfig.Entries) {
                if (m_currentConfig.AsEnumerable().ToList().FindAll(v => v.Key == entry.Key).Count() == 0) {
                    m_currentConfig [entry.Key] = entry.Value;
                }
            }

            RefreshConfigUI();
        }

        private void InitializeConfig(string gameName) {
            var configDir = GetGameConfigDir(gameName);
            var configPath = configDir + "\\config.txt";
            InitializeConfig_FromPath(configPath);
        }
        #endregion configs
        #region proclist
        // will never be used but I'll just move it down here I guess
        private bool IsUnrealEngineGame(string gameDirectory, string targetName) {
            try {
                if (targetName.ToLower().EndsWith("-win64-shipping")) {
                    return true;
                }

                if (targetName.ToLower().EndsWith("-wingdk-shipping")) {
                    return true;
                }

                // Check if going up the parent directories reveals the directory "\Engine\Binaries\ThirdParty".
                var parentPath = gameDirectory;
                for (int i = 0; i < 10; ++i) {  // Limit the number of directories to move up to prevent endless loops.
                    if (parentPath == null) {
                        return false;
                    }

                    if (Directory.Exists(parentPath + "\\Engine\\Binaries\\ThirdParty")) {
                        return true;
                    }

                    if (Directory.Exists(parentPath + "\\Engine\\Binaries\\Win64")) {
                        return true;
                    }

                    parentPath = System.IO.Path.GetDirectoryName(parentPath);
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }

            return false;
        }

        private bool m_isFirstProcessFill = true;

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            //ComboBoxItem comboBoxItem = ((sender as ComboBox).SelectedItem as ComboBoxItem);

            try {
                var box = (sender as ComboBox);
                if (box == null || box.SelectedIndex < 0 || box.SelectedIndex > m_processList.Count) {
                    return;
                }

                var p = m_processList [box.SelectedIndex];
                try {
                    if (p == null || p.HasExited) {
                        if (!IsProcessRunning(p))
                            return;
                    }
                    try {
                        var mainmodule = p.MainModule;
                    } catch {
                        if (IsProcessRunning(p)) {
                            var action = 0;
                            string message = "Process is verified as an Unreal Engine game but a handle could not be obtained.\n";
                            var isAdmin = IsAdministrator();
                            if (!isAdmin) {
                                message += "You may be able to inject by restarting UEVR as admin. Restart now?";
                            } else if (!CheckSetting("ProcessStartTraceInjection")) {
                                message += "Early injection feature is disabled.\nThis is considered unsafe " +
                                "and may cause issues including but not limited to application or even PC shutdown\n" +
                                "This feature simply allows injection into Unreal Engine processes at the moment of creation.\n";
                                action = 1;

                            } else if (!IsProfile(p.ProcessName.ToLower()) && !NullableContains(m_commandLineAttachExe, p.ProcessName)) {
                                message += "Early injection is enabled but no profile exists and UEVR did not start with the process name on the command line.\n." +
                                    "Whitelist the process and attempt to restart it? If it cannot be restarted you must do so manually";
                                action = 2;
                            } else if (DateTime.Compare(p.StartTime, m_uevrStartTime) < 0) {
                                message += "Process started before UEVR. Try restarting it";
                                action = 3;
                            }

                            var dialog = new YesNoDialog("Error Detecting Process", message);
                            dialog.ShowDialog();
                            dialog.btnNo.Content = "No";
                            dialog.Topmost = true;
                            dialog.BringIntoView();
                            dialog.UpdateLayout();
                            dialog.Activate();
                            var yes = dialog.DialogResultYes;
                            if (yes) {

                                if (action == 0)
                                    RestartAsAdmin();
                                else if (action == 1)
                                    UpdateSetting("ProcessStartTraceInjection", true);
                                if (action < 3)
                                    m_mainWindowSettings.WhiteListedGames.Add(p.ProcessName);
                                if (!TerminateProcessWMI(p.ProcessName)) {
                                    MessageBox.Show("Could not close process. Manually restart and try again");
                                    return;
                                }


                            }
                        }

                    }

                    m_lastSelectedProcessName = p.ProcessName;
                    m_lastSelectedProcessId = p.Id;


                } catch { }
                // Search for the VR plugins inside the game directory
                // and warn the user if they exist.
                if (m_lastDisplayedWarningProcess != m_lastSelectedProcessName && p.MainModule != null) {
                    m_lastDisplayedWarningProcess = m_lastSelectedProcessName;

                    var gamePath = p.MainModule.FileName;

                    if (gamePath != null) {
                        var gameDirectory = System.IO.Path.GetDirectoryName(gamePath);

                        if (gameDirectory != null) {
                            var pluginsDir = AreVRPluginsPresent(gameDirectory);

                            if (pluginsDir != null) {
                                MessageBox.Show("VR plugins have been detected in the game install directory.\n" +
                                                "You may want to delete or rename these as they will cause issues with the mod.\n" +
                                                "You may also want to pass -nohmd as a command-line option to the game. This can sometimes work without deleting anything.");
                                var result = MessageBox.Show("Do you want to open the plugins directory now?", "Confirmation", MessageBoxButton.YesNo);

                                switch (result) {
                                    case MessageBoxResult.Yes:
                                        NavigateToDirectory(pluginsDir);
                                        break;
                                    case MessageBoxResult.No:
                                        break;
                                }
                                ;
                            }

                            Check_VirtualDesktop();

                            m_iniListView.ItemsSource = null; // Because we are switching processes.
                            InitializeConfig(p.ProcessName);

                            if (!IsUnrealEngineGame(gameDirectory, m_lastSelectedProcessName) && !m_isFirstProcessFill) {
                                MessageBox.Show("Warning: " + m_lastSelectedProcessName + " does not appear to be an Unreal Engine title");
                            }
                        }

                        m_lastDefaultProcessListName = GenerateProcessName(p);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"Exception caught: {ex}");
            }
        }

        private void ComboBox_DropDownOpened(object sender, System.EventArgs e) {
            m_lastSelectedProcessName = "";
            m_lastSelectedProcessId = 0;

            FillProcessList();
            //Update_InjectStatus(null);

            m_isFirstProcessFill = false;
        }
        #endregion

        #region links

        // standard shell execute fails to launch from just a url if chrome is default browser and is already running as the same user
        // old version most likely worked due to most people running as admin and chrome running as their user profile
        // this new functions checks if the default browser is chrome and then launches with additional arg to support already running proc
        private void Donate_Clicked(object sender, RoutedEventArgs e) {
            LaunchURL("https://patreon.com/praydog");
        }

        private void Documentation_Clicked(object sender, RoutedEventArgs e) {
            LaunchURL("https://praydog.github.io/uevr-docs/");
        }
        private void Discord_Clicked(object sender, RoutedEventArgs e) {
            LaunchURL("http://flat2vr.com");
        }
        private void GitHub_Clicked(object sender, RoutedEventArgs e) {
            LaunchURL("https://github.com/praydog/UEVR");
        }
        #endregion
        #region old_inject

        internal void TerminateConnectedProcess() {
            try {
                if (m_connectedProc is not null) {
                    m_connectedProc.Kill();
                }
                var pid = m_lastSharedData?.pid;
                if (pid is not null) {
                    if (TerminateProcessWMI(pid)) {
                        m_connected = false;
                        m_pid = null;
                        return;
                    }
                } else if (m_pid is not null) {
                    if (TerminateProcessWMI(m_pid)) {
                        m_connected = false;
                        m_pid = null;
                        return;
                    }
                } else {
                    m_connected = false;
                    m_pid = null;
                    return;
                }
            } catch (Exception) { }
            return;
        }

        private void Inject_Clicked(object sender, RoutedEventArgs e) {
            // "Terminate Connected Process"
            if (m_connected) {
                try {
                    var pid = m_lastSharedData?.pid;

                    if (pid != null) {
                        var target = Process.GetProcessById((int)pid);
                        target.CloseMainWindow();
                        target.Kill();
                    }
                } catch (Exception) {

                }

                return;
            }

            var selectedProcessName = m_processListBox.SelectedItem;

            if (selectedProcessName == null) {
                return;
            }

            var index = m_processListBox.SelectedIndex;
            var process = m_processList [index];

            if (process == null) {
                return;
            }

            // Double check that the process we want to inject into exists
            // this can happen if the user presses inject again while
            // the previous combo entry is still selected but the old process
            // has died.
            try {
                var verifyProcess = Process.GetProcessById((int)m_lastSelectedProcessId!);

                if (verifyProcess == null || verifyProcess.HasExited || verifyProcess.ProcessName != m_lastSelectedProcessName) {
                    var processes = Process.GetProcessesByName(m_lastSelectedProcessName);

                    if (processes == null || processes.Length == 0 || !AnyInjectableProcesses(processes)) {
                        return;
                    }

                    foreach (var candidate in processes) {
                        if (IsInjectableProcess(candidate)) {
                            process = candidate;
                            break;
                        }
                    }

                    m_processList [index] = process;
                    m_processListBox.Items [index] = GenerateProcessName(process);
                    m_processListBox.SelectedIndex = index;
                }
            } catch (Exception ex) {
                MessageBox.Show(ex.Message);
                return;
            }

            if (m_nullifyVRPlugins == true) {
                IntPtr nullifierBase;
                if (Injector.InjectDll((uint)(process.Id), "UEVRPluginNullifier.dll", out nullifierBase) && nullifierBase.ToInt64() > 0) {
                    if (!Injector.CallFunctionNoArgs((uint)process.Id, "UEVRPluginNullifier.dll", nullifierBase, "nullify", true)) {
                        MessageBox.Show("Failed to nullify VR plugins.");
                    }
                } else {
                    MessageBox.Show("Failed to inject plugin nullifier.");
                }
            }

            if (Injector.InjectDll((uint)(process.Id), m_runtime)) {
                try {
                    if (m_currentConfig != null) {
                        if (m_currentConfig ["Frontend_RequestedRuntime"] != m_runtime) {
                            m_currentConfig ["Frontend_RequestedRuntime"] = m_runtime;
                            RefreshConfigUI();
                            SaveCurrentConfig();
                        }
                    }
                } catch (Exception) {

                }

                Injector.InjectDll((uint)(process.Id), "UEVRBackend.dll");
            }

            if (m_focusGameOnInjectionCheckbox.IsChecked == true) {
                SwitchToThisWindow(process.MainWindowHandle, true);
            }
        }

        private string GenerateProcessName(Process p) {
            return p.ProcessName + " (pid: " + p.Id + ")" + " (" + p.MainWindowTitle + ")";
        }

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process([In] IntPtr hProcess, [Out] out bool wow64Process);

        private SemaphoreSlim m_processSemaphore = new SemaphoreSlim(1, 1); // create a semaphore with initial count of 1 and max count of 1
        private string? m_lastDefaultProcessListName = null;

        public bool SettingsMenuOpen { get => settingsMenuOpen; set => settingsMenuOpen = value; }

        private async void FillProcessList() {
            // Allow the previous running FillProcessList task to finish first
            if (m_processSemaphore.CurrentCount == 0) {
                return;
            }

            await m_processSemaphore.WaitAsync();

            try {
                m_processList.Clear();
                m_processListBox.Items.Clear();

                var injectableProcesses = await Task.Run(() => {
                    // get the list of processes
                    var processList = new List<Process>();
                    // loop through the list of processes
                    foreach (Process process in Process.GetProcesses()) {
                        if (IsInjectableProcess(process)) {
                            processList.Add(process);
                        }
                    }
                    processList.Sort((a, b) => a.ProcessName.CompareTo(b.ProcessName));
                    return processList;
                });
                m_processList.Clear();
                m_processList.AddRange(injectableProcesses);
                m_processListBox.Items.Clear();

                foreach (var p in m_processList) {
                    string processName = GenerateProcessName(p);
                    m_processListBox.Items.Add(processName);

                    if (m_processListBox.SelectedItem == null && m_processListBox.Items.Count > 0) {
                        if (m_lastDefaultProcessListName == null || m_lastDefaultProcessListName == processName) {
                            m_processListBox.SelectedItem = m_processListBox.Items [m_processListBox.Items.Count - 1];
                            m_lastDefaultProcessListName = processName;
                        }
                    }
                }
            }
           finally {
                m_processSemaphore.Release();
            }
        }

        #endregion
        #region xamlbinds
        // these allow dragging and dropping files to import profiles
        // as well as dragging the main window from any background area rather than only the top bar

        private void MainWindow_PreviewDragEnter(object sender, DragEventArgs e) {
            m_windowHasFocus = false;
            isDragFile = true;
            e.Handled = true;
        }

        private void MainWindow_PreviewDrop(object sender, DragEventArgs e) {
            m_windowHasFocus = false;
            isDragFile = true;
            e.Handled = true;
        }

        private void MainWindow_PreviewDragOver(object sender, DragEventArgs e) {
            e.Handled = true;
            isDragFile = true;
        }

        private void MainWindow_LostMouseCapture(object sender, MouseEventArgs e) {
            m_windowHasFocus = false;
            m_hasMouse = false;

            canDragWindow = false;
        }

        private void MainWindow_LeftButtonUp(object sender, MouseButtonEventArgs e) {
            m_mouseClicked = false;
        }

        private void MainWindow_LeftButtonDown(object sender, MouseButtonEventArgs e) {
            m_mouseClicked = true;
        }

        private void MainWindow_Drop(object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                m_windowHasFocus = false;
                canDragWindow = false;
                string [] files = (string [])e.Data.GetData(DataFormats.FileDrop);
                var file = files [0];
                try {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".zip", StringComparison.Ordinal)) {
                        ImportConfig_Impl(file);
                    } else if (ext.Equals(".lua", StringComparison.Ordinal) && !string.IsNullOrEmpty(m_currentConfigPath)) {
                        File.Copy(file, Path.Combine(Path.GetDirectoryName(m_currentConfigPath)!, "Scripts", Path.GetFileName(file)));

                    }
                } catch (Exception ex) {
                }
            }
        }

        private void ImportConfig_PreviewDragEnter(object sender, DragEventArgs e) {
            isDragFile = true;
            e.Handled = true;
        }
        private void ImportConfig_PreviewDragOver(object sender, DragEventArgs e) {
            isDragFile = true;
            e.Handled = true;
        }


        // TODO change to allow dropping from anywhere on the app
        private void ImportConfig_Drop(object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                string [] files = (string [])e.Data.GetData(DataFormats.FileDrop);
                var file = files [0];
                try {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".zip"))
                        ImportConfig_Impl(file);
                    // allow copying any file into the general config dir with handling to put lua scripts and plugins in their respective dirs
                    else if (m_currentConfigPath is not null) {
                        var confDir = Path.GetDirectoryName(m_currentConfigPath);
                        var newPath = ext.Equals(".dll") ?
                            Path.Combine(confDir!, "plugins", Path.GetFileName(file)) :
                            ext.Equals(".lua") ? Path.Combine(confDir!, "scripts", Path.GetFileName(file)) :
                            Path.Combine(confDir!, Path.GetFileName(file));
                        if (!Directory.Exists(Path.GetDirectoryName(newPath)))
                            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                        File.Copy(file, newPath);
                        MessageBox.Show($"Copied {Path.GetFileName(file)} to config directory");
                    }

                } catch (Exception ex) {
                    MessageBox.Show("An error occurred: " + ex.Message);
                }
            }
        }

        // there's probably a better way to wire the settings menu so that we can reference the xaml objects from here
        // but instead I'll just have the menu handle calling this to update everything
        // well later on I ended up just making shit public in mainwindow... probably inconsistent whatever
        public void UpdateMainWindowSettings() {
            if (m_mainWindowSettings.OpenVRRadio == true) {
                m_runtime = "openvr_api.dll";
            } else if (m_mainWindowSettings.OpenXRRadio == true) {
                m_runtime = "openxr_loader.dll";
            } else {
                m_runtime = "openvr_api.dll";
            }
            if (m_mainWindowSettings.IgnoreFutureVDWarnings == true) {
                m_ignoreFutureVDWarnings = true;
            } else {
                m_ignoreFutureVDWarnings = false;
            }
            if (m_mainWindowSettings.NullifyVRPlugins == true) {
                m_nullifyVRPlugins = true;
            } else {
                m_nullifyVRPlugins = false;
            }
            if (m_mainWindowSettings.FocusGameOnInjection == true) {
                m_focusGameOnInjection = true;
            } else {
                m_focusGameOnInjection = false;
            }

        }

        private void MainWindow_LostFocus(object sender, RoutedEventArgs e) {
            m_windowHasFocus = false;
            m_hasMouse = false;
        }


        private void MainWindow_GotMouseCapture(object sender, MouseEventArgs e) {
            m_hasMouse = true;
        }

        private void MainWindow_MouseMove(object sender, MouseEventArgs e) {
            //canDragWindow = m_hasMouse && m_mouseClicked;
            //if (canDragWindow) {
            try {
                if (e.LeftButton == MouseButtonState.Pressed) {
                    this.DragMove();
                }
            } catch (Exception) {
                return;
            }
            //}
        }


        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            m_mouseClicked = true;
        }


        public bool IsSettingsMenuOpen() {
            m_hasMouse = false;
            m_mouseClicked = false;
            return m_menuToggle.IsChecked == true || SettingsMenuOpen;
        }

        // can be called by tray context menu
        public void OpenSettingsMenu() {
            // show the main window first so the settings arent just floating
            this.Activate();
            m_menuToggle.IsChecked = true;
        }


        private void MainWindow_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {

        }

        private void MainWindow_GotFocus(object sender, RoutedEventArgs e) {
            m_windowHasFocus = true;
        }

        private async void MainWindow_Activated(object sender, EventArgs e) {
            if (WindowState == WindowState.Minimized) {
                WindowState = WindowState.Normal;
            }
            this.Focus();
        }


        private async void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) {
            await Update_InjectorConnectionStatus().WaitAsync(new CancellationToken());

            ShowInTaskbar = this.Visibility == Visibility.Visible ? true : false;
        }

        private void MainWindow_LayoutUpdated(object sender, EventArgs e) {

        }

        private void m_openvrRadio_Checked(object sender, RoutedEventArgs e) {
            m_runtime = "openvr_api.dll";
        }

        private void m_openxrRadio_Checked(object sender, RoutedEventArgs e) {
            m_runtime = "openxr_loader.dll";
        }

        private void m_nullifyVRPluginsCheckbox_Checked(object sender, RoutedEventArgs e) {
            m_nullifyVRPlugins = true;
        }

        private void m_focusGameOnInjectionCheckbox_Checked(object sender, RoutedEventArgs e) {
            m_focusGameOnInjection = true;
        }

        private void m_focusGameOnInjectionCheckbox_Unchecked(object sender, RoutedEventArgs e) {
            m_focusGameOnInjection = false;
        }

        private void m_nullifyVRPluginsCheckbox_Unchecked(object sender, RoutedEventArgs e) {
            m_nullifyVRPlugins = false;
        }

        private void Border_MouseMove(object sender, MouseEventArgs e) {

            try {
                if (e.LeftButton == MouseButtonState.Pressed) {
                    this.DragMove();
                }
            } catch (Exception) {
                return;
            }

        }

        private void m_versionSelectorPopup_Loaded(object sender, RoutedEventArgs e) {

        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            m_mouseClicked = false;
        }
    }

        #endregion xamlbinds

}
#region native
public class MemoryInfo {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX() {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public static void GetMemoryStatus(out double totalMemoryMB, out double availableMemoryMB) {
        var memoryStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memoryStatus)) {
            totalMemoryMB = ConvertBytesToMB(memoryStatus.ullTotalPhys);
            availableMemoryMB = ConvertBytesToMB(memoryStatus.ullAvailPhys);
        } else {
            throw new InvalidOperationException("Failed to retrieve memory status.");
        }
    }
    private static double ConvertBytesToMB(ulong bytes) {
        return bytes / (1024.0 * 1024.0);
    }
}
#endregion