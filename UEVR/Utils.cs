using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

/*
    Copyright lobotomyx 2026
    This file and all code within falls under MIT License and is free to use, modify, distribute, etc. under those terms. 
    The same terms may or may not apply to the rest of the repository which is owned and maintained by praydog.

*/

// Static Helper Classes
namespace UEVR.Utils
{
    // very little UEVR specific info here, could easily be adapted to fit other projects
    public static class GitAPI
    {
        #region serialize
        public class Asset
        {
            public string? Name { get; set; }
            public string? Browser_Download_Url { get; set; }
        }

        public class GitHubResponseObject
        {
            public string? Tag_Name { get; set; }
            public List<Asset>? Assets { get; set; }

            public DateTime? Published_At { get; set; }
            public DateTime? Created_At { get; set; }
            public string? Name { get; set; }
            public long Id { get; set; }
        }


        #endregion
        public static async Task<List<GitHubResponseObject>> GetAllReleasesAsync(
    HttpClient client, string agentName, string repoReleasesUrl)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(agentName);

                string response = await client.GetStringAsync(repoReleasesUrl);
            if ( string.IsNullOrEmpty(response) )
                return new List<GitHubResponseObject>();

            return JsonSerializer.Deserialize<List<GitHubResponseObject>>(
                response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new List<GitHubResponseObject>();
        }

        public static GitHubResponseObject? GetResponseByDisplayName(UpdateClient client, string displayName)
        {

            DateTime displayDateTime = DateTime.Parse(displayName);
            foreach (var release in client.ReleaseList)
            {
                if ( release is null ) continue;
                if (release.Published_At == displayDateTime )
                {
                    return release;
                }
            }
            return client.ReleaseList.First();
        }


        public static async Task<bool> CheckForUpdate( UpdateClient session, string revision)
        {
            session.Client.DefaultRequestHeaders.UserAgent.ParseAdd("UEVR");
            var getAll = GetAllReleasesAsync(session.Client, "UEVR", "https://api.github.com/repos/praydog/uevr-nightly/releases");
            await getAll;
            session.ReleaseList = getAll.Result;
            var latestVersion = session.ReleaseList.OrderByDescending(r => r.Published_At).First();
            session.Latest = ( GitHubResponseObject )latestVersion;
            foreach ( var release in session.ReleaseList )
            {
                if ( release is null ) continue;
                if ( release.Tag_Name.Contains(revision))
                {
                    var dt = ( DateTime )release.Published_At; 
                    if (dt.CompareTo((DateTime)latestVersion.Published_At) < 0 )
                    {
                        return true;
                    }
                }
            }
            session.DisposeAsync();
            return false;
        }


        public static List<string> BuildVersionList(List<GitHubResponseObject> releaseObjects)
        { 
            List<string> releases = new List<string>();
            try
            {
               
                releaseObjects = releaseObjects.OrderByDescending(r => r.Published_At).ToList();
                foreach ( var obj in releaseObjects )
                {
                    if ( obj is null ) continue;
                    releases.Add(obj.Published_At.ToString());
                }
            }
            catch { }
            return releases;
        }


        // avoids remaking a client
        public sealed class UpdateClient : IAsyncDisposable
        {
            public HttpClient Client { get; } = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            public string? DownloadUrl { get; set; }
            public string? TagName { get; set; }
            public GitHubResponseObject? Latest { get; set; }



            public List<GitHubResponseObject> ReleaseList { get; set; }

            public ValueTask DisposeAsync()
            {
                Client.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        // compares our local revision to the latest nightly and returns to the parent task
        // two steps because we need to ask users without autoupdate enabled if they want the update
        public static async Task<bool> CheckForUpdateAsync(UpdateClient session, string agentName, string repoUrl, DateTime? lastUpdate)
        {
            session.Client.DefaultRequestHeaders.UserAgent.ParseAdd(agentName);

            string response = await session.Client.GetStringAsync(repoUrl);
            if (string.IsNullOrEmpty(response))
                return false;

            var release = JsonSerializer.Deserialize<GitHubResponseObject>(
                response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (lastUpdate is not null && ((DateTime)lastUpdate).CompareTo(release.Published_At) >= 0 )
            {
                return false;
            }
                
            var asset = release?.Assets?
                .FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

            if (asset == null)
                return false;

            session.DownloadUrl = asset.Browser_Download_Url;

            return true;
        }

 
     


        // mainwindow will handle the next steps
        // the caller must ensure the path is safe to write to e.g. with Path.TempFile
        public static async Task<bool> DownloadUpdateAsync(UpdateClient session, string downloadPath)
        {
            if (session.DownloadUrl is null)
                return false;

            using var resp = await session.Client.GetAsync(
                session.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead);

            if (!resp.IsSuccessStatusCode)
                return false;
            try
            {
                await using var fs = new FileStream(
                    downloadPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);

                await resp.Content.CopyToAsync(fs);
            }
            catch
            {

            }
            return true;
        }

        public static async Task DownloadSpecificRelease(GitHubResponseObject? Release, string downloadPath)
        {
            await using var session = new UpdateClient();
            if (Release is not null )
            {
                var asset = Release?.Assets?
            .FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);


                session.DownloadUrl = asset.Browser_Download_Url;
                session.TagName = Release.Tag_Name;



                await DownloadUpdateAsync(session, downloadPath);
            }




    
        }
    

        public static async Task RunUpdateTasks(UpdateClient session, string url, DateTime? lastUpdate, string userAgentName, string downloadPath, bool automaticUpdates, Task<bool>? allowSingleUpdate)
        {

            bool updateAvailable = await CheckForUpdateAsync(session, userAgentName, url, lastUpdate);
            if (!updateAvailable)
                return;

            if (!automaticUpdates)
            {
                if (allowSingleUpdate is not null)
                {
                    var result = await allowSingleUpdate;
                    if (!result) return;
                }
            }

            await DownloadUpdateAsync(session, downloadPath);
        }
    }


    public static class Nullables
    {
        public static bool NullableEquals(object? nullable, object? other)
        {
            if (nullable is null) return false;
            if (other is null) return false;
            if (other.GetType() != nullable.GetType()) return false;
            return nullable == other;
        }
        public static bool NullableContains(string? nullable, string? other)
        {
            if (nullable is null) return false;
            if (other is null) return false;
            if (other.Length == 0) return false;
            return nullable.Contains(other, StringComparison.InvariantCultureIgnoreCase);
        }
        public static bool Contains(List<string>? list, string? term) {
            if (list is null) return false;
            if (term is null) return false;
            if (list.Count== 0) return false;
            foreach(var s in list) {
                if (NullableContains(s, term)) {
                    return true;
                }
            }
            return false;
        }
    }

    // UAC must be enabled to launch processes without elevation
    // in that case the user would be running UEVR as admin as well which might be okay 
    // Not currently using
    public static class UacHelper
    {
        // Registry key path for UAC settings
        private const string UacRegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string EnableLUAValueName = "EnableLUA";
        public static bool IsUacEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UacRegistryKeyPath))
                {
                    if (key != null)
                    {
                        object enableLUAValue = key.GetValue(EnableLUAValueName);

                        if (enableLUAValue != null && enableLUAValue is int)
                        {
                            // UAC is enabled if the value is non-zero (typically 1).
                            return (int)enableLUAValue != 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // if we failed to read it then we probably are running as a user with low perms so we'll assume its on
                return false;
            }

            return false;
        }
    }

    public static class BrowserHelper {
        [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern uint AssocQueryString(
        AssocF flags, AssocStr str, string pszAssoc, string pszExtra,
        [Out] StringBuilder pszOut, [In][Out] ref uint pcchOut);

        public enum AssocStr {
            Command = 1,
            Executable = 2,
            FriendlyDocName = 3,
            FriendlyAppName = 4
        }

        [Flags]
        public enum AssocF {
            None = 0,
            Verify = 0x40
        }

        public static string GetDefaultBrowserPath() {
            const string userChoicePath = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";

            using (RegistryKey userChoiceKey = Registry.CurrentUser.OpenSubKey(userChoicePath)) {
                if (userChoiceKey == null) return "Edge (Fallback)";

                // Get the ProgId (e.g., "ChromeHTML")
                object progIdValue = userChoiceKey.GetValue("ProgId");
                if (progIdValue == null) return "Edge (Fallback)";

                string progId = progIdValue.ToString();

                // Now find the command for this ProgId
                using (RegistryKey commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command")) {
                    if (commandKey == null) return null;

                    // Returns something like: "C:\...\chrome.exe" -- "%1"
                    string command = commandKey.GetValue(null)?.ToString();
                    return command?.Split('"') [1]; // Extract just the EXE path
                }
            }
        }

        public static void LaunchURL(string? url) {
            try {
                url = url!.Replace("&", "^&");
                var p = Process.Start(new ProcessStartInfo(url) {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    LoadUserProfile = true
                });
                if (p is null) {
                    var browser = GetDefaultBrowserPath();
                    if (browser.EndsWith("chrome.exe", StringComparison.InvariantCultureIgnoreCase)) {
                        Process.Start(new ProcessStartInfo() {
                            FileName = browser,
                            Arguments = $"--new-window \"{url}\"",
                        });
                    }
                }
            } catch { }
        }
    }
    /*
        There's a weird void of real knowledge about shortcuts
        If you search for how to make one almost all info points 
        towards using ancient WScript stuff from powershell
        Or encourages you to add a COM reference to your project
        Or even worse add a big nuget package to be able to work with the lnk binary format
        But you can literally just pinvoke the actual winapi stuff like anything else in windows
        This is used exclusively by the settings menu but felt odd to bury inside a xaml.cs class
    */
    public static class ShortcutHelper
    {

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        public class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }


        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }


        // Lame and stupid method using powershell (still better than actually using COM in C#)
        public static void CreateShortcutPS(string shortcutPath, string targetFileLocation)
        {
            var cmd = "-Command \"$s=(New-Object -COM WScript.Shell).CreateShortcut(" +
                $"'{shortcutPath}');" +
                "$s.TargetPath=" +
               $"'{targetFileLocation}';" +
               "$s.IconLocation = $s.TargetPath + ', 0';$s.Save()\"";
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        public static string? GetShortcutTarget(string shortcutPath)
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }

        public static string? GetShortcutArguments(string shortcutPath)
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var sb = new StringBuilder(260);
            link.GetArguments(sb, sb.Capacity);
            return sb.ToString();
        }


        public static void CreateShortcutNative(string shortcutPath, string target, string? args = null, string? iconPath = null, int? windowStyle = null)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            if (args is not null) link.SetArguments(args);
            link.SetIconLocation(iconPath is not null ? (string)iconPath : target, 0);
            link.SetShowCmd(windowStyle is not null ? (int)windowStyle : 1); // 1 = default, 3 = maximized, 7 = minimized
            var persist = (IPersistFile)link;
            persist.Save(shortcutPath, true);
        }

        /*      
                 Used to create startup shortcuts for EGS and Steam to keep them from unnecessarily elevating
                 Which has the effect of carrying over to games, also very unnecessary
                 Games that truly need to elevate, e.g. to run an anticheat service, can still do so by asking permission
                 launchers can ask for privileges to install games and can have their background services running
                 So this doesn't do anything weird or concerning
                 rather it stops those programs from doing weird, concerning things
                 With this setup you basically never need to run as admin which is ideal.

                 Note that because this is actually making a shortcut to cmd rather than the intended targets
                 the actual link has to be retrieved with GetShortcutArguments

                     var steamArgs = GetShortcutArguments(_SteamLnkPath);
                     var _SteamTargetPath = Path.GetFullPath(steamArgs.Split(" ").Last());
                  
                it is possible to directly launch processes with environment args in C# so it may
                also be possible from a shortcut. Direct launch isn't an option since we need to intercept normal startup with windows
                Compat Layer options are not really well exposed or documented in windows

        */

        public static void CreateUnelevatedShortcut(string targetFileLocation, string shortcutPath)
        {
            CreateShortcutNative(
                shortcutPath,
                "cmd.exe",
                // launch minimized cmd window and set the env var for the session
                // this env var makes it so processes will not automatically try to elevate
                // this only works if the user has UAC enabled which most should
                // but disabling it to run all games as admin does improve performance so its more common than one might think
                "/min /C " + "\"set __COMPAT_LAYER=RUNASINVOKER && start \"\" \"" + targetFileLocation + "\"",
                targetFileLocation,
                7
            );
        }

        public static void DeleteShortcut(string shortcutPath)
        {
            try {
                File.Delete(shortcutPath);
                if (!File.Exists(shortcutPath))
                    return;
            } catch { }

            var cmd = "-Command \"Remove-Item -Path \"" +
                $"{shortcutPath}" +
                "\" -Force";

            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        public static void UpdateShortcutTarget(string shortcutPath, string? newTargetPath)
        {
            if (newTargetPath is null) return;
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            link.SetPath(newTargetPath);
            ((IPersistFile)link).Save(shortcutPath, true);
        }


        public static string GetShellStartupPath(string? shortcutName = null)
        {
            var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            if (!Directory.Exists(startup))
            {
                Directory.CreateDirectory(startup);
            }
            return shortcutName is null ? startup : Path.Combine(startup, shortcutName);
        }

    }

    public static class ProcessManagement
    {
        /*
            Things we can do to elevated processes while unelevated
             and to protected processes while elevated
                - check if they're running
                - get the pid, process name, and title
                - check if they're responding
                - get memory usage
                - get lifetime info
                - subscribe to events
                - get mainwindow handle and title
                - use mainwindow handle to get window class
                - use wmi to get commandline and modules
                - kill
                - list threads
                - pass the mainwindowhandle or pid to an elevated service to inject
                  or in the case of protected processes, inject if early enough
                - enable window hooks
           Things we cannot do
                - inject without a service
                - enable raising events
                - view modules
                - list handle count
                - suspend, resume, create threads
                
        */

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            TERMINATE = 0x0001,
            SUSPEND_RESUME = 0x0800,
            // can open elevated and protected processes and get minimal info
            QUERY_LIMITED_INFORMATION = 0x1000,
            ALL_ACCESS = 0x1FFFFF,
        };


        [DllImport("ntdll.dll")]
        public static extern uint RtlAdjustPrivilege(uint Privilege, bool bEnablePrivilege, bool IsThreadPrivilege, out bool PreviousValue);


        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr hprocess, int dwFlags, StringBuilder lpExeName, out int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hHandle);

        public static bool IsCurrentProcessElevated()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static void LaunchProcessUnelevated(string procPath)
        {
            if (IsProcessRunning(Path.GetFileNameWithoutExtension(procPath), out Process? p))
            {
                if (IsProcessElevated(p))
                {
                    try
                    {
                        TerminateProcessWMI((int?)p.Id);
                    }
                    catch { }
                }
            }

            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "cmd.exe",
                Arguments = @$"/min /C ""set __COMPAT_LAYER=RUNASINVOKER && start """" ""{procPath}""""",
                UseShellExecute = false,
                CreateNoWindow = true,
                LoadUserProfile = true
            };
            Process.Start(startInfo);
        }

        // More accurately this is checking if we can likely restart as an admin and get a handle
        // Meaning that it will return false if the process is dead or kernel protected
        public static bool IsProcessElevated(Process? p)
        {
            if (p is null || p.HasExited) return false;
            try
            {
                var handle = p.SafeHandle;
            }
            // Relying on an exception is not ideal but there's not many other ways to do this
            // probably the most reliable would be to have an elevated service that opens the process to get the token but that kind of defeats the purpose
            catch (Win32Exception e)
            {

                // if we are elevated
                if (!IsCurrentProcessElevated())
                    return true;
                else
                    return false;
            }

            return false;
        }
        public static bool IsProcessRunning(Process? p)
        {
            if (p is null) return false;
            if (p.HasExited) return false;
            return true;
        }

        public static bool IsProcessRunning(string name, out string? path)
        {
            path = null;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p is null || p.HasExited) return false;
                try
                {
                    if (p.Responding)
                    {
                        return GetExecutablePath(p, out path);
                    }
                }
                catch { }
            }
            return false;
        }

        public static bool IsProcessRunning(string name, out Process? proc)
        {
            proc = null;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p is null || p.HasExited) return false;
                try
                {
                    if (p.Responding)
                    {
                        proc = p;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }


        public static bool IsProcessRunning(string name, out Process? proc, out string? path)
        {
            proc = null;
            path = null;
            return IsProcessRunning(name, out path) && IsProcessRunning(name, out proc);
        }

        public static bool IsProcessRunning(int id)
        {
            var proc = Process.GetProcessById(id);
            if (proc is null)
                return false;
            else Console.WriteLine(proc.MainWindowTitle);
            return true;
        }

        public static DateTime? GetProcessStartTime(int id)
        {
            var proc = Process.GetProcessById(id);
            if (proc is null)
                return null;
            return proc.StartTime;
        }

        // Pinvoke option for getting full path from elevated procs
        public static bool GetExecutablePath(Process p, out string? path)
        {
            path = null;
            int processId = p.Id;
            if (IsCurrentProcessElevated())
            {
                try
                {
                    var mod = p.MainModule;
                    if (mod is not null)
                    {
                        path = mod.FileName;
                        if (path is not null)
                            return true;
                    }
                }
                catch { }
            }
            else if (IsProcessElevated(p))  // This should basically only come up if its actually an issue of the other proc being elevated
            {
                var buffer = new StringBuilder(1024);
                // Query limited information flag was added alongside protected processes and should let us get bare minimum info for anything non-kernel
                try
                {
                    IntPtr hProcess = OpenProcess(ProcessAccessFlags.QUERY_LIMITED_INFORMATION, false, (uint)processId);
                    if (hProcess == IntPtr.Zero)
                    {
                        // couldn't open a handle so we'll try WMI
                        return GetExecutablePath(processId, out path);
                    }
                    int size = buffer.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, buffer, out size))
                    {
                        path = buffer.ToString();
                        CloseHandle(hProcess);
                        return true;
                    }
                    else
                    {
                        CloseHandle(hProcess);
                        return GetExecutablePath(processId, out path);
                    }
                }
                catch { }
            }
            else if (p is not null && !p.HasExited) // If we ended up here its probably a protected process or has exited
            {
                // we'll try WMI in case its just protected
                return GetExecutablePath(processId, out path);
            }
            return false;
        }


        // wmi fallback
        // This way we don't need an actual Process handle
        public static bool GetExecutablePath(int pid, out string? path)
        {
            path = null;
            // Construct the WQL query
            string wqlQuery = $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}";

            try
            {
                // Connect to WMI and execute the query
                using (var searcher = new ManagementObjectSearcher(wqlQuery))
                {
                    foreach (ManagementObject process in searcher.Get())
                    {
                        // The CommandLine property contains the full command used to start the process
                        path = process["ExecutablePath"]?.ToString();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying WMI: {ex.Message}");
            }

            return false;
        }

        // this will get us protected and elevated names to show in the main window
        // id will be obtained by scanning for unreal window
        public static string GetExecutableName(int pid)
        {
            try
            {
                // Generally we should be able to get this, I've not seen a case where its impossible
                // But it may depend on some windows setting or access rights
                Process? p = Process.GetProcessById(pid);
                if (p is not null)
                    return p.ProcessName;
            }
            catch (Exception ex)
            {
            }
            try {
                string q = $"SELECT Name FROM Win32_Process WHERE ProcessId = {pid}";
                using var searcher = new ManagementObjectSearcher(q);
                foreach ( ManagementObject obj in searcher.Get() )
                {
                    return obj [ "Name" ]?.ToString() ?? "(null)";
                }
            }
            catch ( Exception ex )
            {
                return "WMI ERROR: " + ex.Message;
            }
            return "(not found)";
        }

        // normally you can only get this if you monitored process creation or use pinvoke
        public static bool GetCommandLine(int pid, out string? commandLine)
        {
            commandLine = null;
            // Construct the WQL query
            string wqlQuery = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}";

            try
            {
                // Connect to WMI and execute the query
                using (var searcher = new ManagementObjectSearcher(wqlQuery))
                {
                    foreach (ManagementObject process in searcher.Get())
                    {
                        commandLine = process["CommandLine"]?.ToString();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error querying WMI: {ex.Message}");
            }

            return false;
        }

        public static bool TerminateProcessNative(int? pid)
        {
            if (pid is not null)
            {
                try
                {
                    IntPtr hProcess = OpenProcess(ProcessAccessFlags.TERMINATE, false, (uint)pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        if (TerminateProcess(hProcess, 0))
                        {
                            if (GetExitCodeProcess(hProcess, out uint exitCode))
                            {
                                return true;
                            }
                        }

                    }
                }
                catch (Exception ex)
                {

                }
            }
            return false;
        }


        public static bool TerminateProcessWMI(int? pid)
        {
            if (pid is not null)
            {
                try
                {
                    string query = $"SELECT * FROM Win32_Process WHERE ProcessId = {pid}";
                    ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
                    ManagementObjectCollection processes = searcher.Get();
                    foreach (ManagementObject process in processes)
                    {
                        uint returnValue = (uint)process.InvokeMethod("Terminate", null);

                        if (returnValue == 0)
                            return true;
                    }

                    if (processes.Count == 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex) { }
            }
            return false;
        }
        public static bool TerminateProcessWMI(string? name)
        {
            if (name is not null)
            {
                try
                {
                    string query = $"SELECT * FROM Win32_Process WHERE ProcessName = {name}";
                    ManagementObjectSearcher searcher = new ManagementObjectSearcher(query);
                    ManagementObjectCollection processes = searcher.Get();
                    foreach (ManagementObject process in processes)
                    {
                        uint returnValue = (uint)process.InvokeMethod("Terminate", null);

                        if (returnValue == 0)
                            return true;
                    }

                    if (processes.Count == 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex) { }
            }
            return false;
        }

    }

    public static class CleanupScheduler
    {
        public static async Task DeleteWhenUnlockedAsync(string path, CancellationToken token = default, Process? proc = null)
        {
            if (proc is not null)
            {
                await proc.WaitForExitAsync();
            }
            if ( !File.Exists(path) && !Directory.Exists(path)) return;

            while ( true )
            {
                token.ThrowIfCancellationRequested();

                var lockers = FileLockInspector.GetLockingProcesses(path);

                if ( lockers.Count == 0 )
                {
                    try
                    {
                        if ( Directory.Exists(path) ) Directory.Delete(path);
                        else if (File.Exists(path)) File.Delete(path);
                        return;
                    }
                    catch ( IOException )
                    {
                    }
                    catch ( UnauthorizedAccessException )
                    {
                        if (Application.Current.MainWindow.Visibility == Visibility.Visible)
                            MessageBox.Show(Application.Current.MainWindow, $"Failed to delete {path}", "Warning");
                    }
                }

                if ( lockers.Count > 0 )
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int remaining = lockers.Count;

                    void OnExit(object? s, EventArgs e)
                    {
                        if ( Interlocked.Decrement(ref remaining) == 0 )
                            tcs.TrySetResult(true);
                    }

                    var hooked = new List<Process>();
                    try
                    {
                        foreach ( var p in lockers.DistinctBy(p => p.Id) )
                        {
                            try
                            {
                                if ( p.HasExited ) continue;
                                p.EnableRaisingEvents = true;
                                p.Exited += OnExit;
                                hooked.Add(p);
                            }
                            catch { }
                        }

                        if ( hooked.Count == 0 )
                        {
                            // nothing to wait on, back off a bit
                            await Task.Delay(500, token);
                        }
                        else
                        {
                            await Task.WhenAny(tcs.Task, Task.Delay(10000, token));
                        }
                    }
                    finally
                    {
                        foreach ( var p in hooked )
                        {
                            try { p.Exited -= OnExit; } catch { }
                        }
                    }
                }

                await Task.Delay(250, token);
            }
        }
    }


    public static class FileLockInspector
    {
        private const int RmRebootReasonNone = 0;

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(
            out uint pSessionHandle,
            int dwSessionFlags,
            string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string [ ] rgsFilenames,
            uint nApplications,
            [In] RM_UNIQUE_PROCESS [ ] rgApplications,
            uint nServices,
            string [ ] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO [ ] rgAffectedApps,
            out uint lpdwRebootReasons);

        public static List<Process> GetLockingProcesses(string path)
        {
            var result = new List<Process>();
            uint handle;
            string key = Guid.NewGuid().ToString();
            int res = RmStartSession(out handle, 0, key);
            if ( res != 0 ) return result;

            try
            {
                string [ ] resources = { path };
                res = RmRegisterResources(handle, ( uint )resources.Length, resources, 0, null, 0, null);
                if ( res != 0 ) return result;

                uint needed = 0;
                uint count = 0;
                uint reboot;
                res = RmGetList(handle, out needed, ref count, null, out reboot);
                if ( res == 234 ) // ERROR_MORE_DATA
                {
                    var infos = new RM_PROCESS_INFO [ needed ];
                    count = needed;
                    res = RmGetList(handle, out needed, ref count, infos, out reboot);
                    if ( res == 0 )
                    {
                        for ( int i = 0; i < count; i++ )
                        {
                            try
                            {
                                var pid = infos [ i ].Process.dwProcessId;
                                var p = Process.GetProcessById(pid);
                                result.Add(p);
                            }
                            catch { }
                        }
                    }
                }
            }
            finally
            {
                RmEndSession(handle);
            }

            return result;
        }
    }


    public static class WindowUtils
    {
        #region native

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowTextLengthA(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);


        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetWindowTextA(IntPtr hWnd, out string? lpString, uint nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? WindowClass, string? WindowName);


        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? WindowClass, string? WindowName);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr hWnd);    

        delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern System.UInt16 RegisterClassW(
           [In] ref WNDCLASS lpWndClass
       );


        [DllImport("user32.dll", SetLastError = true)]
        static extern System.IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);


        // Delegate for the EnumWindows callback function
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);


        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);


        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
#endregion
        public static List<IntPtr> EnumerateUnrealWindows()
        {
            List<IntPtr> windows = new List<IntPtr>();

            EnumWindows((hWnd, lParam) =>
            {
                const int MAX_CLASS_NAME_LENGTH = 256;
                StringBuilder classNameBuilder = new StringBuilder(MAX_CLASS_NAME_LENGTH);

                if (GetClassName(hWnd, classNameBuilder, MAX_CLASS_NAME_LENGTH) > 0)
                {
                    if (classNameBuilder.ToString() == "UnrealWindow")
                    {
                        windows.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        public static bool CheckProcessForUnrealWindow(Process p)
        {
            if (p is null) return false;
            return CheckProcessForUnrealWindow((uint)p.Id);
        }

        public static bool CheckProcessForUnrealWindow(uint pId)
        {
            // Okay so Code Vein 2 came out as the first unreal engine game in existence to not have an unreal window
            // the game also crashes very easily so idk if its even a game we need to consider but whatever

            // FindWindow searches all windows and processes so if this gets nothing there is no unreal process running 
            IntPtr unrealWindow = FindWindow("UnrealWindow", null);
            if (unrealWindow == IntPtr.Zero)
            {
                return false;
            }

            // FindWindow just grabs the first one it can find so we'll check that first 
            _ = GetWindowThreadProcessId(unrealWindow, out uint processId);
            if (pId == processId)
                return true;

            // We found an unreal window that didn't belong to our process so we now have to scan all windows
            try
            {
                var windows = EnumerateUnrealWindows();
                foreach (var wnd in windows)
                {
                    // skip the original window
                    if (wnd == unrealWindow) continue;

                    // get process id for this window
                    _ = GetWindowThreadProcessId(wnd, out uint otherPid);

                    if (otherPid == pId)
                        return true;
                    else
                        continue;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        public static uint FindUnrealWindow()
        {
            IntPtr unrealWindow = FindWindow("UnrealWindow", null);
            if (unrealWindow == IntPtr.Zero)
            {
                return 0;
            }
            var excludedTitles = new string[] { "launcher", "epicgameslauncher", "crashreportclient", "ue4editor", "unrealeditor", "livecoding", "unrealinsights", "unrealswitchboard", "unrealfrontend", "livelinkhub", "zendashboard" };
            // Get the process id for the initially found window
            var tid = GetWindowThreadProcessId(unrealWindow, out uint processId);

            try
            {
                // Attempt to get the process name for the initial window
                string procName = "";
                try
                {
                    var proc = Process.GetProcessById((int)processId);
                    procName = proc?.MainModule?.FileName?.ToLowerInvariant() ?? "";
                }
                catch
                {
                    Utils.ProcessManagement.GetExecutablePath((int)processId, out string? executable_path);
                    if (executable_path is not null) {
                        procName = Path.GetFileName(executable_path).ToLowerInvariant();
                    }
                }
                bool excluded = false;
                // If the initial window belongs to an excluded process, look for other UnrealWindow class windows
                foreach (var proc in excludedTitles)
                {
                    if (procName.Contains(proc)) { excluded = true; break; }
                }
                if (excluded)
                {
                    var windows = EnumerateUnrealWindows();

                    foreach (var wnd in windows)
                    {
                        // skip the original window
                        if (wnd == unrealWindow) continue;

                        // get process id for this window
                        var hresult = GetWindowThreadProcessId(wnd, out uint otherPid);

                        if (otherPid == 0 || otherPid == processId) continue;

                        try
                        {
                            var otherProc = Process.GetProcessById((int)otherPid);
                            var otherName = otherProc?.MainModule?.FileName?.ToLowerInvariant() ?? "";
                            bool other_excluded = false;
                            // return the first UnrealWindow that does NOT belong to an excluded process
                            foreach (var proc in excludedTitles)
                            {
                                if (otherName.Contains(proc)) { other_excluded = true; break; }
                            }
                            if (!other_excluded)
                            {
                                return otherPid;
                            }
                        }
                        catch
                        {
                            // ignore processes we cannot inspect and continue searching
                            continue;
                        }
                    }
                }
                else
                {
                    return processId;
                }
            }
            catch (Exception)
            {
                // fall back to returning the originally discovered process id
            }
            return 0;
        }
    }
}



