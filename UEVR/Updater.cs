using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEVR.Utils;
using static UEVR.Utils.GitAPI;

namespace UEVR {
    /// <summary>
    /// Handles UEVR-specific update logic (nightly/stable releases, rollback, extraction).
    /// UI classes should call into this instead of duplicating update code.
    /// </summary>
    public class Updater {
        private readonly string _globalDir;
        private readonly string _backendDir;
        private readonly string _releasesUrl = "https://api.github.com/repos/praydog/uevr-nightly/releases";
        private readonly string _userAgent = "UEVR";
        private readonly string _revisionPath;
        private readonly string _cachePath;

        public Updater() {
            _globalDir = MainWindow.GetGlobalDir();
            _backendDir = Path.Combine(_globalDir, "UEVR");
            _revisionPath = Path.Combine(_backendDir, "revision.txt");
            _cachePath = Path.Combine(_backendDir, "releases_cache.json");
        }

        /// <summary>
        /// Reads the current installed revision string from revision.txt.
        /// </summary>
        public string? GetCurrentRevision() {
            try {
                if (File.Exists(_revisionPath))
                    return File.ReadAllText(_revisionPath).Trim();
            } catch { }
            return null;
        }

        /// <summary>
        /// Loads cached releases if available, otherwise fetches fresh from GitHub.
        /// </summary>
        public async Task<List<GitHubResponseObject>> GetReleasesAsync() {
            List<GitHubResponseObject> releases = new();
            try {
                if (File.Exists(_cachePath)) {
                    var cached = JsonSerializer.Deserialize<List<GitHubResponseObject>>(
                        File.ReadAllText(_cachePath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (cached?.Count > 0)
                        releases = cached.OrderByDescending(r => r.Published_At).ToList();
                } else {
                    var fresh = await GetAllReleasesAsync(new System.Net.Http.HttpClient(), _userAgent, _releasesUrl);
                    if (fresh?.Count > 0) {
                        releases = fresh.OrderByDescending(r => r.Published_At).ToList();
                        File.WriteAllText(_cachePath, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            } catch { }
            return releases;
        }

        /// <summary>
        /// Checks if a newer nightly release is available compared to current revision.
        /// </summary>
        public bool IsUpdateAvailable(string? currentRevision, List<GitHubResponseObject> releases, out GitHubResponseObject? Update) {
            Update = null;
            if (string.IsNullOrEmpty(currentRevision) || releases.Count == 0)
                return false;

            var latest = releases.FirstOrDefault();
            if (latest == null) return false;
            Update = latest;
            return !latest.Tag_Name!.Contains(currentRevision, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Downloads and extracts the given release into the backend directory.
        /// </summary>
        public async Task<bool> DownloadAndExtractAsync(GitHubResponseObject release, CancellationToken token = default) {
            if (release?.Assets == null) return false;
            var asset = release.Assets.FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
            if (asset == null) return false;

            var downloadPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), $"UEVR_{Guid.NewGuid()}");

            await using var session = new UpdateClient { DownloadUrl = asset.Browser_Download_Url };
            if (!await DownloadUpdateAsync(session, downloadPath))
                return false;

            try {
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(downloadPath, extractDir, true);

                var contents = new string []
                {
                    "openxr_loader.dll",
                    "revision.txt",
                    "UEVRBackend.dll",
                    "UEVRBackend.pdb",
                    "UEVRInjector.dll.config",
                    "UEVRInjector.exe",
                    "UEVRInjector.pdb",
                    "UEVRPluginNullifier.dll",
                    "LuaVR.dll",
                    "openvr_api.dll",
                };

                foreach (var file in Directory.GetFiles(_backendDir)) {
                    var fname = Path.GetFileName(file);
                    if (contents.Contains(fname) && !fname.Equals("UEVRInjector.exe", StringComparison.OrdinalIgnoreCase)) {
                        try { File.Delete(file); } catch { Debug.WriteLine($"Failed to delete {file}"); }
                    }
                }

                foreach (var file in Directory.GetFiles(extractDir)) {
                    var fname = Path.GetFileName(file);
                    if (contents.Contains(fname) && !fname.Equals("UEVRInjector.exe", StringComparison.OrdinalIgnoreCase)) {
                        var dest = Path.Combine(_backendDir, fname);
                        try {
                            if (File.Exists(dest)) File.Delete(dest);
                            File.Move(file, dest);
                        } catch {
                            try { File.Copy(file, dest, true); File.Delete(file); } catch { }
                        }
                    }
                }

                return true;
            } catch (Exception ex) {
                Debug.WriteLine($"Update extraction failed: {ex}");
                return false;
            }
        }
    }
}