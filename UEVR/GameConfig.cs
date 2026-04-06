using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using static UEVR.Utils.CleanupScheduler;
namespace UEVR
{
    public static class GameConfig
    {
        // files that can show up if the backend hooked d3d or initialized a vr runtime
        // this happens before any engine scans and can easily occur if the user didn't check they had the right app selected with the old version
        // or if they intentionally injected into a game they mistakenly thought was UE, e.g. a unity game
        static readonly string[] uevrFiles = {
            "actions.json",
            "binding_rift.json",
            "binding_vive.json",
            "bindings_knuckles.json",
            "bindings_oculus_touch.json",
            "bindings_vive_controller.json",
            "cameras.txt",
            "imgui.ini",
            "config.txt",
            };

        // files that would only be present if the user installed a profile or made them
        static readonly string[] userFiles = {
            "cvardump.json",
            "cvars_data.txt",
            "cvars_standard.txt",
            "user_script.txt"
            };

        static readonly string[] epicApps = {
            "launcher",
            "crashreport",
            "ue4editor",
            "unrealeditor",
            "livecoding",
            "unrealinsights",
            "unrealswitchboard",
            "unrealfrontend",
            "livelinkhub",
            "epicgameslauncher",
            "zendashboard",
            };

        public static string UnrealVRMod()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod");
        }
        
        public static bool IsEpicApp(string? prof) {
            if (epicApps.Any(d => prof!.ToLower().Contains(d))) return true;
            return false;
        }

        public static bool IsEpicApp(Process? p) {
            string? prof = Utils.ProcessManagement.GetExecutableName(p.Id); 
            if (epicApps.Any(d => prof!.ToLower().Contains(d))) return true;
            return false;
        }


        #region export

        // https://stackoverflow.com/questions/19395128/c-sharp-zipfile-createfromdirectory-the-process-cannot-access-the-file-path-t
        public static void CreateZipFromDirectory(string sourceDirectoryName, string destinationArchiveFileName)
        {
            try
            {
                using (FileStream zipToOpen = new FileStream(destinationArchiveFileName, FileMode.Create))
                using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create))
                {
                    DirectoryInfo di = new DirectoryInfo(sourceDirectoryName);
                    string basePath = di.FullName;

                    // Recursive method to process directories
                    ProcessDirectory(di, basePath, archive);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred: " + ex.Message);
            }
        }

        // users will be warned if they import a config with a plugin so it should be fine to include them
        static readonly string[] directories_to_pack = {
            "plugins", "uobjecthook", "scripts", "data", "fonts"
            };


        private static void ProcessDirectory(DirectoryInfo di, string basePath, ZipArchive archive)
        {
            // Process files in the directory
            foreach (FileInfo file in di.GetFiles())
            {
                try
                {
                    string entryName = GetRelativePath(file.FullName, basePath);
                    if (!uevrFiles.Contains(entryName))
                    {
                        if (!(Path.GetExtension(entryName) == ".txt" || Path.GetExtension(entryName) == ".json"))
                            continue;
                    }
                    var entry = archive.CreateEntry(entryName);
                    entry.LastWriteTime = file.LastWriteTime;
                    using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var stream = entry.Open())
                    {
                        fs.CopyTo(stream);
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            // Recursively process subdirectories
            // inclusive makes more sense than exclusive since you can have a bunch of junk that gets included 
            // e.g. anyone using our dumper-7 builds might have a bad time
            foreach (DirectoryInfo subDi in di.GetDirectories())
            {
                if (directories_to_pack.Any(s => s.Contains(subDi.Name, StringComparison.InvariantCultureIgnoreCase)))
                {
                    ProcessDirectory(subDi, basePath, archive);
                }
            }
        }

        private static string GetRelativePath(string fullPath, string basePath)
        {
            // Ensure trailing backslash
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                basePath += Path.DirectorySeparatorChar;
            }

            return fullPath.Substring(basePath.Length);
        }
        #endregion
        #region import
        public static bool ZipContainsDLL(string sourceArchiveFileName)
        {
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(sourceArchiveFileName))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.ToLower().EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }

            // No .DLL files found
            return false;
        }

        public static string? ExtractZipToDirectory(string sourceArchiveFileName, string destinationDirectoryName, string gameName)
        {
            try
            {
                string tempExtractionPath = Path.Combine(destinationDirectoryName, "temp_extraction");
                Directory.CreateDirectory(tempExtractionPath);

                ZipFile.ExtractToDirectory(sourceArchiveFileName, tempExtractionPath, overwriteFiles: true);

                var extractedEntries = Directory.GetFileSystemEntries(tempExtractionPath);
                if (extractedEntries.Length == 1)
                {
                    var singleEntry = extractedEntries[0];

                    // Check if the single entry is a zip file
                    if (File.Exists(singleEntry) && Path.GetExtension(singleEntry).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        string nestedZipName = Path.GetFileNameWithoutExtension(singleEntry);
                        string nestedDestination = Path.Combine(destinationDirectoryName, "..", nestedZipName);
                        Directory.CreateDirectory(nestedDestination);

                        ZipFile.ExtractToDirectory(singleEntry, nestedDestination, overwriteFiles: true);
                        File.Delete(singleEntry);

                        Directory.Delete(tempExtractionPath, true);
                        return nestedZipName;
                    }

                    // Check if the single entry is a directory with a matching name
                    if (Directory.Exists(singleEntry) && Path.GetFileName(singleEntry).Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    {
                        MoveDirectoryContents(singleEntry, destinationDirectoryName);
                        Directory.Delete(tempExtractionPath, true);
                        return gameName;
                    }
                }

                // Move extracted files from temp directory to final destination
                MoveDirectoryContents(tempExtractionPath, destinationDirectoryName);
                Directory.Delete(tempExtractionPath, true);
                return gameName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred: " + ex.Message);
                return null;
            }
        }

        private static void MoveDirectoryContents(string sourceDir, string destinationDir)
        {
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourceDir, destinationDir));
            }

            foreach (var newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourceDir, destinationDir), true);
            }
        }

        public static string? BrowseForImport(string? initialDirectory = null)
        {
            var openFileDialog = new OpenFileDialog
            {
                DefaultExt = ".zip",
                Filter = "Zip Files (*.zip)|*.zip",
                InitialDirectory = initialDirectory
            };

            bool? result = openFileDialog.ShowDialog();
            if (result == true)
            {
                return openFileDialog.FileName;
            }

            return null;
        }

        #endregion

        #region management

        public static List<string> GetProfileNames()
        {
            List<string> tempProfiles = new List<string>();
            List<string> profiles = new List<string>();
            try
            {
                string unrealvrmod = UnrealVRMod();
                tempProfiles = Directory.GetDirectories(unrealvrmod).Where(Directory.Exists).ToList();
                foreach (var prof in tempProfiles)
                {
                    if (IsValidProfile(prof))
                    {
                        profiles.Add(prof);
                    }
                }
            }
            catch { }
            return profiles;
        }

        public static bool IsProfile(string path)
        {
            string unrealvrmod = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnrealVRMod");
            return Directory.Exists(Path.Combine(unrealvrmod, path)) && CheckSubdir(path) != 0;
        }




        public static int CheckSubdir(string path)
        {
            var count = 0;
            try
            {
                if (!Directory.Exists(path))
                    return 0;
                else count = Directory.GetFileSystemEntries(path).Length;
            }
            catch (Exception) { }
            if (count == 0)
            {
                Directory.Delete(path);
            }
            return count;
        }

        public static bool ValidateLog(string? path)
        {
            try
            {
                if (path is null) return false;
                var log = Path.Combine(UnrealVRMod(), path, "log.txt");
                if (!File.Exists(path)) return false;
                foreach (var line in File.ReadLines(path))
                {
                    foreach (var goodLine in new string[] {
                        "Found SlateRHIRenderer::DrawWindow_RenderThread",
                        "FFakeStereoRendering VTable", "Hooked UGameEngine::Tick!",
                        "Framework initialized",
                        "Found InitializeHMDDevice",
                        "Found object base init function",
                        "Found string references for FSceneView constructor",
                        "Found cvar for \"r.EnableStereoEmulation\"" })
                    {
                        if (line.Contains(goodLine, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }



        public static bool IsValidProfile(string? path)
        {
            if (path is null) return false;
            var prof = Directory.GetParent(path)!.ToString().EndsWith("UnrealVRMod") ? path : Path.Combine(UnrealVRMod(), path);
            string profLower = prof.ToLowerInvariant();
            if (profLower.EndsWith("win64-shipping")) return true;
            // obviously not a valid profile but we don't want to delete it
            if (profLower.Contains("uevr")) return true;
            // obviously not a valid profile but we don't want to delete it
            if (profLower.EndsWith("ExportedConfigs")) return true;
            if (epicApps.Any(d => profLower.Contains(d))) return false;
            // these would only exist if the user intentionally did something
            if (File.Exists(Path.Combine(prof, "user_script.txt"))) return true;
            if (File.Exists(Path.Combine(prof, "cvardump.json"))) return true;

            // the typical invalid prof will just be a log.txt and maybe an empty plugins folder
            // TryCleanSubdir will delete empty subdirectories as we go or otherwise return the filecount
            int totalFileCount = CheckSubdir(Path.Combine(prof, "plugins"))
            + CheckSubdir(Path.Combine(prof, "scripts"))
            + CheckSubdir(Path.Combine(prof, "uobjecthook"))
            + CheckSubdir(Path.Combine(prof, "sdkdump"))
            + CheckSubdir(Path.Combine(prof, "fonts"))
            + CheckSubdir(Path.Combine(prof, "data"));
            if (totalFileCount > 0) return true;
            // if we don't have these files then we've never successfully initialized so there's no harm in deleting
            if (!uevrFiles.Any(f => File.Exists(Path.Combine(prof, f)))) return false;
            // finally we check the log. there are plenty of reasons why a valid game could fail to initialize so its a last resort
            return ValidateLog(Path.Combine(prof, "log.txt"));
        }

        public static async Task<bool> TryRemoveProfile(string path)
        {
            if (!IsValidProfile(path))
            {
                foreach (var file in uevrFiles)
                {
                    if (File.Exists(Path.Combine(path, file)))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch { }
                    }
                }
                try
                {
                    Directory.Delete(path, true);
                    return true;
                }
                catch
                {
                    await DeleteWhenUnlockedAsync(path);
                    if (Directory.Exists(path)) {
                        return false;
                    }
                }
            }
            return false;
        }

        public static async void Cleanup()
        {
            var deletedProfiles = new List<string>();
            var junkProfiles = new List<string>();
            try
            {
                var profiles = Directory.GetDirectories(UnrealVRMod());

                foreach (var prof in profiles)
                {
                    if (!IsValidProfile(prof))
                    {
                        junkProfiles.Add(prof);
                    }
                }
                // move wd out of the folder otherwise deleting can fail

                if (junkProfiles.Count() == 0) {

                    return;
                }

                string message = "Found " + junkProfiles.Count() + " invalid or empty profiles." +
                        "Delete the invalid profiles? (Only empty folders and autogenerated files will be affected)";
                if (junkProfiles.Count() == 1) {
                    message += $"\n{junkProfiles[0].Split(Directory.GetParent(junkProfiles [0])!.ToString()).Last()}";
                }
                var dialog = new YesNoDialog("Profile Cleanup", message);
                dialog.ShowDialog();
                dialog.btnNo.Content = "Review Manually";
                dialog.Topmost = true;
                dialog.BringIntoView();
                dialog.UpdateLayout();
                dialog.Activate();
                var wants_cleanup = dialog.DialogResultYes;
                var manual_review = !wants_cleanup;
                if (manual_review)
                {
                    foreach (var _prof in junkProfiles)
                    {
                        string _message = "Delete " + Path.GetFileNameWithoutExtension(_prof) + " folder?\nHold Shift to clean all remaining.";
                        var _dialog = new YesNoDialog("Profile Cleanup", _message);
                        _dialog.ShowDialog();
                        _dialog.BringIntoView();
                        _dialog.Topmost = true;
                        dialog.AltYesText = "Delete All Remaining";
                        _dialog.Activate();
                        var _wants_cleanup = _dialog.DialogResultYes;
                        var _cleanall = _dialog.ShiftHeld;
                        if (_cleanall && _wants_cleanup)
                        {
                            wants_cleanup = true;
                            break;
                        }
                        if (_wants_cleanup && TryRemoveProfile(_prof).Result) {
                                deletedProfiles.Add(_prof);
                        }
                    }
                    if (!wants_cleanup) {
                        return;
                    }
                }
                string oldDir = "";
                if (wants_cleanup)
                {
                    oldDir = Directory.GetCurrentDirectory();
                    Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));


                    foreach (var prof in junkProfiles) {
                        var res = TryRemoveProfile(prof);
                        await res;
                        if (res.Result)
                        {
                            deletedProfiles.Add(prof);
                        }
                    }
         
                    if (deletedProfiles.Count < junkProfiles.Count )
                    {
                        foreach(var prof in deletedProfiles )
                        {
                            junkProfiles.Remove(prof);
                        }
                     }
                    Directory.SetCurrentDirectory(oldDir);
                }
            }
            catch (Exception) { }
        }

    }
        #endregion
}