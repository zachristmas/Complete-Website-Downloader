using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WebsiteDownloader.Services
{
    /// <summary>
    /// Downloads websites using Playwright/Crawlee (Node.js) for full JavaScript rendering.
    /// Falls back gracefully with clear error messages when Node.js is not available.
    /// </summary>
    public class PlaywrightDownloader : IWebsiteDownloader
    {
        private readonly IAppLogger _logger;
        private readonly object _processLock = new object();
        private Process _currentProcess;
        private volatile bool _isDownloading;
        private bool _disposed;
        // When reusing a browser profile, the crawl runs against a copy here (see PrepareProfileCopy).
        private string _activeProfileDir;

        /// <inheritdoc/>
        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;

        /// <inheritdoc/>
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        /// <inheritdoc/>
        public bool IsDownloading => _isDownloading;

        /// <summary>
        /// Whether to strip analytics/tracking scripts from saved HTML for cleaner offline viewing.
        /// </summary>
        public bool StripAnalyticsScripts { get; set; } = true;

        /// <summary>
        /// When true, the crawl launches the user's real Chrome/Edge profile (a persistent
        /// context) so it inherits their existing login session instead of a clean Chromium.
        /// </summary>
        public bool UseBrowserProfile { get; set; } = false;

        /// <summary>
        /// Which installed browser channel to drive when <see cref="UseBrowserProfile"/> is set:
        /// "chrome" or "msedge".
        /// </summary>
        public string BrowserChannel { get; set; } = "chrome";

        /// <summary>
        /// Path to the browser "User Data" directory. Empty means auto-detect the default for
        /// <see cref="BrowserChannel"/>.
        /// </summary>
        public string UserDataDir { get; set; } = "";

        /// <summary>
        /// Which profile subfolder within "User Data" to use (e.g. "Default", "Profile 1").
        /// </summary>
        public string ProfileDirectory { get; set; } = "Default";

        /// <summary>
        /// Whether to show the browser window during the crawl (recommended when reusing a
        /// profile, so the user can confirm the session and handle any re-auth prompts).
        /// </summary>
        public bool Headful { get; set; } = true;

        /// <summary>
        /// Path to the crawler runtime directory in AppData.
        /// </summary>
        private static string CrawlerDir => Path.Combine(AppConstants.AppDataFolder, "crawler");

        /// <summary>
        /// Path to the crawler script.
        /// </summary>
        private static string CrawlerScriptPath => Path.Combine(CrawlerDir, "crawler.mjs");

        public PlaywrightDownloader(IAppLogger logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _logger.Debug("PlaywrightDownloader initialized");
        }

        /// <summary>
        /// Checks whether the Playwright engine requirements are met.
        /// </summary>
        public static EngineRequirements CheckRequirements()
        {
            var result = new EngineRequirements();

            // Check Node.js
            result.NodeInstalled = IsCommandAvailable("node", "--version");
            if (result.NodeInstalled)
            {
                result.NodeVersion = GetCommandOutput("node", "--version")?.Trim();
            }

            // Check npm (on Windows it's npm.cmd, use cmd /c)
            result.NpmInstalled = IsCommandAvailable("cmd.exe", "/c npm --version");

            // Check if crawler is set up
            result.CrawlerInstalled = File.Exists(CrawlerScriptPath)
                && Directory.Exists(Path.Combine(CrawlerDir, "node_modules"));

            return result;
        }

        /// <summary>
        /// Sets up the crawler environment (installs npm packages + creates script).
        /// </summary>
        public async Task<bool> SetupAsync(Action<string> progressCallback, CancellationToken cancellationToken)
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(CrawlerDir))
                    Directory.CreateDirectory(CrawlerDir);

                progressCallback?.Invoke("Creating crawler project...");

                // Write package.json
                var packageJson = GetPackageJson();
                File.WriteAllText(Path.Combine(CrawlerDir, "package.json"), packageJson);

                // Write crawler script
                var script = GetCrawlerScript();
                File.WriteAllText(CrawlerScriptPath, script);

                progressCallback?.Invoke("Installing dependencies (crawlee + playwright)...");

                // Run npm install (use cmd /c on Windows since npm is a .cmd file)
                var npmResult = await RunProcessAsync("cmd.exe", "/c npm install", CrawlerDir, cancellationToken, progressCallback);
                if (!npmResult.Success)
                {
                    progressCallback?.Invoke($"npm install failed: {npmResult.Error}");
                    return false;
                }

                progressCallback?.Invoke("Installing Playwright browsers (downloading Chromium)...");

                // Install playwright browsers
                var pwResult = await RunProcessAsync("cmd.exe", "/c npx playwright install chromium", CrawlerDir, cancellationToken, progressCallback);
                if (!pwResult.Success)
                {
                    progressCallback?.Invoke($"Playwright browser install failed: {pwResult.Error}");
                    return false;
                }

                progressCallback?.Invoke("Setup complete!");
                return true;
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"Setup failed: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task DownloadAsync(DownloadOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_isDownloading) throw new InvalidOperationException("A download is already in progress.");

            _isDownloading = true;
            var startTime = DateTime.Now;

            _logger.Info($"Starting Playwright download: {options.Url}");

            try
            {
                var reqs = CheckRequirements();
                if (!reqs.NodeInstalled)
                    throw new InvalidOperationException("Node.js is not installed. Please install Node.js from https://nodejs.org/");

                if (!reqs.CrawlerInstalled)
                    throw new InvalidOperationException("Crawler not set up. Go to Settings → Advanced → Setup Playwright Engine.");

                if (UseBrowserProfile)
                {
                    ValidateBrowserProfile();
                    // Chrome (v136+) blocks remote debugging (which Playwright needs) on the default
                    // profile directory, so drive a copy in a non-default location instead. The copy
                    // carries the login session (cookies + Local State) so the crawl is authenticated.
                    OnProgressChanged("Preparing a private copy of your browser profile...");
                    _activeProfileDir = PrepareProfileCopy(NormalizeChannel(BrowserChannel));
                }

                // Always update the crawler script to latest version
                EnsureLatestScript();

                var outputFolder = Path.Combine(options.OutputFolder, options.Url.Host);

                // Build arguments for the crawler script
                var args = BuildScriptArgs(options);

                _logger.Debug($"Crawler args: {args}");

                _currentProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = args,
                        WorkingDirectory = CrawlerDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                _currentProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        ParseCrawlerOutput(e.Data);
                    }
                };

                _currentProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !e.Data.Contains("ExperimentalWarning"))
                    {
                        OnProgressChanged(e.Data);
                    }
                };

                _currentProcess.Start();
                _currentProcess.BeginOutputReadLine();
                _currentProcess.BeginErrorReadLine();

                bool wasCancelled = false;
                using (cancellationToken.Register(() =>
                {
                    wasCancelled = true;
                    CancelDownload();
                }))
                {
                    try
                    {
                        await Task.Run(() => _currentProcess.WaitForExit()).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        wasCancelled = true;
                    }
                }

                int exitCode = 0;
                try
                {
                    if (_currentProcess != null && _currentProcess.HasExited)
                        exitCode = _currentProcess.ExitCode;
                }
                catch (InvalidOperationException) { }

                var success = !wasCancelled && Directory.Exists(outputFolder);
                var duration = DateTime.Now - startTime;

                if (success)
                    _logger.Info($"Playwright download completed: {options.Url} (Duration: {duration:mm\\:ss})");
                else if (wasCancelled)
                    _logger.Warning($"Playwright download cancelled: {options.Url}");
                else
                    _logger.Error($"Playwright download failed: {options.Url} (Exit code: {exitCode})");

                OnDownloadCompleted(new DownloadCompletedEventArgs
                {
                    Success = success,
                    OutputFolder = outputFolder,
                    Url = options.Url,
                    Duration = duration,
                    Cancelled = wasCancelled,
                    ExitCode = exitCode
                });
            }
            finally
            {
                CleanupProcess();
                CleanupProfileCopy();
                _isDownloading = false;
            }
        }

        /// <inheritdoc/>
        public void CancelDownload()
        {
            lock (_processLock)
            {
                try
                {
                    if (_currentProcess == null || _currentProcess.HasExited)
                        return;

                    _currentProcess.Kill();
                    _currentProcess.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        private string BuildScriptArgs(DownloadOptions options)
        {
            var sb = new StringBuilder();
            sb.Append($"\"{CrawlerScriptPath}\" ");
            sb.Append($"--url \"{options.Url}\" ");
            sb.Append($"--output \"{options.OutputFolder}\" ");
            sb.Append($"--depth {(options.MaxDepth > 0 ? options.MaxDepth : 50)} ");

            if (options.WaitBetweenRequests > 0)
                sb.Append($"--wait {options.WaitBetweenRequests * 1000} ");

            if (options.ConvertLinks)
                sb.Append("--convert-links ");

            // Try sitemap-based discovery for better coverage
            sb.Append("--use-sitemap ");

            if (StripAnalyticsScripts)
                sb.Append("--strip-analytics ");

            if (UseBrowserProfile)
            {
                var channel = NormalizeChannel(BrowserChannel);
                // Use the copy prepared in DownloadAsync; fall back to the real dir if absent.
                var dataDir = _activeProfileDir ?? ResolveUserDataDir(channel);
                var profileDir = string.IsNullOrWhiteSpace(ProfileDirectory) ? "Default" : ProfileDirectory;

                sb.Append($"--browser-channel {channel} ");
                sb.Append($"--user-data-dir \"{dataDir}\" ");
                sb.Append($"--profile-directory \"{profileDir}\" ");
                if (Headful)
                    sb.Append("--headful ");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Normalizes a browser channel string to a Playwright channel id ("chrome" or "msedge").
        /// </summary>
        internal static string NormalizeChannel(string channel)
        {
            return string.Equals(channel, "msedge", StringComparison.OrdinalIgnoreCase)
                ? "msedge" : "chrome";
        }

        /// <summary>
        /// Resolves the "User Data" directory for the given channel, honoring an explicit
        /// <see cref="UserDataDir"/> override and otherwise using the platform default.
        /// </summary>
        internal string ResolveUserDataDir(string channel)
        {
            if (!string.IsNullOrWhiteSpace(UserDataDir))
                return UserDataDir;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return channel == "msedge"
                ? Path.Combine(localAppData, "Microsoft", "Edge", "User Data")
                : Path.Combine(localAppData, "Google", "Chrome", "User Data");
        }

        /// <summary>
        /// Opens the real Chrome/Edge executable using the same channel and profile the crawl
        /// will reuse, so the user can establish or confirm their login session (e.g. sign in
        /// with Google) before downloading. This launches the plain browser — not a Playwright/
        /// CDP-controlled instance — which avoids automation-detection blocks on interactive
        /// logins; the session it creates lives in the same on-disk profile the crawl reuses.
        /// </summary>
        /// <param name="url">Optional URL to open; when null the browser opens normally.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the profile folder or the browser executable cannot be found.
        /// </exception>
        public void LaunchLoginBrowser(string url = null)
        {
            var channel = NormalizeChannel(BrowserChannel);
            var dataDir = ResolveUserDataDir(channel);
            var profileDir = string.IsNullOrWhiteSpace(ProfileDirectory) ? "Default" : ProfileDirectory;
            var friendlyName = channel == "msedge" ? "Edge" : "Chrome";

            if (!Directory.Exists(dataDir))
                throw new InvalidOperationException(
                    $"{friendlyName} profile folder not found: {dataDir}. " +
                    "Set the correct \"User Data\" path in Settings, or pick the other browser.");

            var exePath = ResolveBrowserExecutable(channel);
            if (exePath == null)
                throw new InvalidOperationException(
                    $"Couldn't locate the {friendlyName} executable on this machine. " +
                    $"Make sure {friendlyName} is installed.");

            var args = $"--profile-directory=\"{profileDir}\" --user-data-dir=\"{dataDir}\"";
            if (!string.IsNullOrWhiteSpace(url))
                args += $" \"{url}\"";

            _logger.Info($"Launching {friendlyName} for login: {exePath} {args}");

            // UseShellExecute=false + a fully-resolved path launches the browser directly via
            // CreateProcess (predictable, and surfaces a clear Win32 error if it fails).
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false
            });
        }

        /// <summary>
        /// Path to the working copy of the browser profile the crawl drives.
        /// </summary>
        private static string ProfileCopyDir => Path.Combine(AppConstants.AppDataFolder, "crawler", "profile-copy");

        /// <summary>
        /// Copies the session-bearing parts of the user's profile to a non-default directory and
        /// returns its path. Chrome (v136+) refuses remote debugging on the default profile dir,
        /// so Playwright must drive a copy. Only cookies/storage are copied (not the large caches);
        /// the browser must be closed (enforced by <see cref="ValidateBrowserProfile"/>) for a clean copy.
        /// </summary>
        private string PrepareProfileCopy(string channel)
        {
            var realUserData = ResolveUserDataDir(channel);
            var profileDir = string.IsNullOrWhiteSpace(ProfileDirectory) ? "Default" : ProfileDirectory;
            var copyRoot = ProfileCopyDir;

            CleanupProfileCopy();
            Directory.CreateDirectory(Path.Combine(copyRoot, profileDir));

            // "Local State" (at the User Data root) holds the key that decrypts the cookies.
            var localState = Path.Combine(realUserData, "Local State");
            if (File.Exists(localState))
                File.Copy(localState, Path.Combine(copyRoot, "Local State"), true);

            // Session data from the profile; deliberately excludes Cache/Code Cache/GPUCache etc.
            var srcProfile = Path.Combine(realUserData, profileDir);
            var dstProfile = Path.Combine(copyRoot, profileDir);
            string[] items = { "Network", "Local Storage", "Session Storage", "IndexedDB", "Preferences", "Login Data", "Cookies", "Web Data" };
            foreach (var item in items)
            {
                var src = Path.Combine(srcProfile, item);
                var dst = Path.Combine(dstProfile, item);
                try
                {
                    if (Directory.Exists(src)) CopyDirectory(src, dst);
                    else if (File.Exists(src)) File.Copy(src, dst, true);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Could not copy profile item '{item}': {ex.Message}");
                }
            }

            _logger.Info($"Prepared profile copy at {copyRoot}");
            return copyRoot;
        }

        /// <summary>
        /// Recursively copies a directory.
        /// </summary>
        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        /// <summary>
        /// Deletes the working profile copy (it contains the user's cookies/session, so it is not
        /// left on disk after the crawl).
        /// </summary>
        private void CleanupProfileCopy()
        {
            _activeProfileDir = null;
            try
            {
                if (Directory.Exists(ProfileCopyDir))
                    Directory.Delete(ProfileCopyDir, true);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not delete profile copy: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves the full path to the Chrome/Edge executable, checking the Windows
        /// "App Paths" registry entries first and then common install locations.
        /// Returns null if it cannot be found.
        /// </summary>
        internal static string ResolveBrowserExecutable(string channel)
        {
            var exeName = channel == "msedge" ? "msedge.exe" : "chrome.exe";

            // 1) "App Paths" registry (how the shell resolves a bare "chrome.exe"/"msedge.exe").
            string[] appPathKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
            };
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var key in appPathKeys)
                {
                    try
                    {
                        using (var rk = root.OpenSubKey(key))
                        {
                            if (rk?.GetValue(null) is string path && File.Exists(path))
                                return path;
                        }
                    }
                    catch { /* registry access denied / malformed - fall through */ }
                }
            }

            // 2) Common install locations.
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var candidates = channel == "msedge"
                ? new[]
                {
                    Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
                }
                : new[]
                {
                    Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
                };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Ensures the selected browser profile can be driven: the "User Data" directory must
        /// exist, and the browser must not be running (Chrome/Edge lock the profile while open,
        /// which prevents Playwright from launching a persistent context against it).
        /// </summary>
        private void ValidateBrowserProfile()
        {
            var channel = NormalizeChannel(BrowserChannel);
            var dataDir = ResolveUserDataDir(channel);
            var friendlyName = channel == "msedge" ? "Edge" : "Chrome";

            if (!Directory.Exists(dataDir))
                throw new InvalidOperationException(
                    $"{friendlyName} profile folder not found: {dataDir}. " +
                    "Set the correct \"User Data\" path in Settings, or pick the other browser.");

            var processName = channel == "msedge" ? "msedge" : "chrome";
            bool running;
            try
            {
                running = Process.GetProcessesByName(processName).Length > 0;
            }
            catch
            {
                // If we can't enumerate processes, don't block the download; Playwright will
                // surface a clear launch error if the profile really is locked.
                running = false;
            }

            if (running)
                throw new InvalidOperationException(
                    $"{friendlyName} is currently running, so its profile is locked. " +
                    $"Close ALL {friendlyName} windows — and any {friendlyName} background processes " +
                    "(check the system tray and Task Manager) — then start the download again.");
        }

        private void ParseCrawlerOutput(string line)
        {
            // Our crawler script outputs structured lines: [TYPE] message
            if (line.StartsWith("[SAVED]"))
            {
                var msg = line.Substring(7).Trim();
                OnProgressChanged($"Saving to: {msg}");
            }
            else if (line.StartsWith("[QUEUE]"))
            {
                var msg = line.Substring(7).Trim();
                OnProgressChanged($"Discovered: {msg}");
            }
            else if (line.StartsWith("[STATUS]"))
            {
                var msg = line.Substring(8).Trim();
                OnProgressChanged(msg);
            }
            else if (line.StartsWith("[ERROR]"))
            {
                var msg = line.Substring(7).Trim();
                OnProgressChanged($"ERROR: {msg}");
            }
            else if (line.StartsWith("[DONE]"))
            {
                var msg = line.Substring(6).Trim();
                OnProgressChanged($"FINISHED -- {msg}");
            }
            else
            {
                OnProgressChanged(line);
            }
        }

        private void OnProgressChanged(string message)
        {
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs { Message = message });
        }

        private void OnDownloadCompleted(DownloadCompletedEventArgs args)
        {
            DownloadCompleted?.Invoke(this, args);
        }

        private void CleanupProcess()
        {
            lock (_processLock)
            {
                if (_currentProcess != null)
                {
                    _currentProcess.Dispose();
                    _currentProcess = null;
                }
            }
        }

        /// <summary>
        /// Ensures the crawler script on disk is the latest version.
        /// </summary>
        private static void EnsureLatestScript()
        {
            var script = GetCrawlerScript();
            if (!Directory.Exists(CrawlerDir))
                Directory.CreateDirectory(CrawlerDir);
            File.WriteAllText(CrawlerScriptPath, script);
        }

        private static bool IsCommandAvailable(string command, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(5000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetCommandOutput(string command, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    return output;
                }
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ProcessResult> RunProcessAsync(string fileName, string args, string workDir, CancellationToken ct, Action<string> outputCallback = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        outputCallback?.Invoke(e.Data);
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        outputCallback?.Invoke(e.Data);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using (ct.Register(() => { try { proc.Kill(); } catch { } }))
                {
                    await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                }

                return new ProcessResult
                {
                    Success = proc.ExitCode == 0,
                    Output = outputBuilder.ToString(),
                    Error = errorBuilder.ToString()
                };
            }
        }

        private static string GetPackageJson()
        {
            return @"{
  ""name"": ""website-downloader-crawler"",
  ""version"": ""1.0.0"",
  ""type"": ""module"",
  ""private"": true,
  ""dependencies"": {
    ""crawlee"": ""^3.8.0"",
    ""playwright"": ""^1.42.0""
  }
}";
        }

        private static string GetCrawlerScript()
        {
            return @"import { PlaywrightCrawler } from 'crawlee';
import { writeFile, mkdir, readFile } from 'fs/promises';
import { dirname, join } from 'path';
import { URL } from 'url';
import https from 'https';
import http from 'http';

// Parse CLI arguments
const args = process.argv.slice(2);
function getArg(name) {
    const idx = args.indexOf(name);
    return idx >= 0 && idx + 1 < args.length ? args[idx + 1] : null;
}
function hasFlag(name) {
    return args.includes(name);
}

const startUrl = getArg('--url');
const outputBase = getArg('--output');
const maxDepth = parseInt(getArg('--depth') || '50', 10);
const waitMs = parseInt(getArg('--wait') || '0', 10);
const convertLinks = hasFlag('--convert-links');
const useSitemap = hasFlag('--use-sitemap');
const stripAnalytics = hasFlag('--strip-analytics');

// Browser-profile reuse: when a channel + user-data-dir are supplied, drive the user's real
// Chrome/Edge profile (a persistent context) so the crawl inherits their logged-in session.
const browserChannel = getArg('--browser-channel');
const userDataDir = getArg('--user-data-dir');
const profileDirectory = getArg('--profile-directory') || 'Default';
const headful = hasFlag('--headful');
const useProfile = !!(browserChannel && userDataDir);

// Playwright/Crawlee can emit a browser launch/close race during shutdown (after the crawl
// has already saved pages). Don't let that async error crash the process with a huge stack.
const isTeardownNoise = (err) => {
    const m = (err && err.message) ? err.message : String(err);
    return m.includes('has been closed') || m.includes('Failed to launch browser');
};
process.on('unhandledRejection', (err) => {
    if (!isTeardownNoise(err)) console.log(`[ERROR] ${(err && err.message) || err}`);
});
process.on('uncaughtException', (err) => {
    if (!isTeardownNoise(err)) console.log(`[ERROR] ${(err && err.message) || err}`);
});

if (!startUrl || !outputBase) {
    console.error('Usage: node crawler.mjs --url <url> --output <dir> [--depth N] [--wait ms] [--convert-links] [--use-sitemap]');
    process.exit(1);
}

const startUrlObj = new URL(startUrl);
const hostDir = join(outputBase, startUrlObj.hostname);

// Use directory path for filtering
let basePath = startUrlObj.pathname;
if (!basePath.endsWith('/')) {
    const lastSlash = basePath.lastIndexOf('/');
    basePath = basePath.substring(0, lastSlash + 1);
}

let savedCount = 0;
let assetCount = 0;
const savedAssets = new Set();

// Same-site helpers: treat all subdomains of the registrable domain (e.g. assets.<site>) as
// part of the site so their assets are captured and rewritten for offline viewing.
const DQ = String.fromCharCode(34);
const SQ = String.fromCharCode(39);
function baseDomainOf(h) { const p = h.split('.'); return p.length <= 2 ? h : p.slice(-2).join('.'); }
const SITE_BASE = baseDomainOf(startUrlObj.hostname);
function isSameSite(h) { return h === startUrlObj.hostname || h === SITE_BASE || h.endsWith('.' + SITE_BASE); }
// On-disk path (relative to the site folder, leading '/') for a same-site resource. Assets on
// other subdomains are namespaced under /_ext/<host>/ to avoid collisions.
function localAssetPath(host, pathname) { return host === startUrlObj.hostname ? pathname : ('/_ext/' + host + pathname); }
function stripQuotes(s) {
    s = (s || '').trim();
    if (s.length >= 2 && ((s[0] === DQ && s[s.length - 1] === DQ) || (s[0] === SQ && s[s.length - 1] === SQ))) return s.slice(1, -1).trim();
    return s;
}
// Map a reference (absolute URL / //host / root-absolute path) to a same-site on-disk path,
// or null to leave it unchanged (external host, data:, already-relative, etc.).
function refToLocal(ref) {
    if (!ref) return null;
    ref = ref.trim();
    if (ref === '' || ref.startsWith('data:') || ref.startsWith('#') || ref.startsWith('mailto:') || ref.startsWith('tel:') || ref.startsWith('javascript:') || ref.startsWith('blob:')) return null;
    if (/^https?:\/\//i.test(ref) || ref.startsWith('//')) {
        try {
            const u = new URL(ref.startsWith('//') ? ('https:' + ref) : ref);
            if (!isSameSite(u.hostname)) return null;
            return localAssetPath(u.hostname, u.pathname);
        } catch { return null; }
    }
    if (ref.startsWith('/') && !ref.startsWith('//')) return ref.split('?')[0].split('#')[0];
    return null;
}
// Rewrite same-site references in HTML or CSS to paths relative to `dir` (site-root-relative).
function rewriteRefs(content, dir) {
    const urlStop = '[^\\s' + SQ + DQ + ')>]';
    content = content.replace(new RegExp('https?://' + urlStop + '+', 'gi'), function(m) {
        const local = refToLocal(m);
        return local ? relPath(dir, local) : m;
    });
    content = content.replace(/url\(([^)]*)\)/gi, function(m, inner) {
        const local = refToLocal(stripQuotes(inner));
        return local ? ('url(' + relPath(dir, local) + ')') : m;
    });
    content = content.replace(new RegExp(DQ + '(/(?!/)[^' + DQ + ']*)' + DQ, 'g'), function(m, p) {
        const local = refToLocal(p);
        return local ? (DQ + relPath(dir, local) + DQ) : m;
    });
    content = content.replace(new RegExp(SQ + '(/(?!/)[^' + SQ + ']*)' + SQ, 'g'), function(m, p) {
        const local = refToLocal(p);
        return local ? (SQ + relPath(dir, local) + SQ) : m;
    });
    return content;
}

// Download a URL as text
function fetchText(url) {
    return new Promise((resolve, reject) => {
        const lib = url.startsWith('https') ? https : http;
        lib.get(url, { headers: { 'User-Agent': 'Mozilla/5.0' } }, (res) => {
            if (res.statusCode !== 200) { reject(new Error(`HTTP ${res.statusCode}`)); return; }
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => resolve(data));
        }).on('error', reject);
    });
}

// Parse sitemap XML and return all <loc> URLs
function parseSitemapUrls(xml) {
    const urls = [];
    const locRegex = /<loc>([^<]+)<\/loc>/g;
    let match;
    while ((match = locRegex.exec(xml)) !== null) {
        urls.push(match[1].trim());
    }
    return urls;
}

// Discover URLs from sitemap
async function discoverFromSitemap() {
    const sitemapPaths = ['/sitemap.xml', '/sitemap-index.xml', '/sitemap_index.xml'];
    const baseUrl = `${startUrlObj.protocol}//${startUrlObj.host}`;
    
    for (const path of sitemapPaths) {
        try {
            console.log(`[STATUS] Checking for sitemap at ${baseUrl}${path}`);
            const xml = await fetchText(`${baseUrl}${path}`);
            
            // Check if it's a sitemap index
            if (xml.includes('<sitemapindex')) {
                console.log(`[STATUS] Found sitemap index, parsing sub-sitemaps...`);
                const subUrls = parseSitemapUrls(xml);
                let allUrls = [];
                for (const subUrl of subUrls) {
                    try {
                        console.log(`[STATUS] Parsing ${subUrl}...`);
                        const subXml = await fetchText(subUrl);
                        allUrls = allUrls.concat(parseSitemapUrls(subXml));
                    } catch (e) {
                        console.log(`[ERROR] Failed to parse ${subUrl}: ${e.message}`);
                    }
                }
                return allUrls;
            }
            
            // Regular sitemap
            return parseSitemapUrls(xml);
        } catch {
            continue;
        }
    }
    return [];
}

// Save a network response to disk
async function saveAsset(url, body) {
    try {
        const u = new URL(url);
        if (!isSameSite(u.hostname)) return;

        const localPath = localAssetPath(u.hostname, u.pathname);
        if (savedAssets.has(localPath)) return;
        savedAssets.add(localPath);

        let out = body;
        // Rewrite url()/@import inside CSS so fonts and background images resolve offline.
        if (/\.css(\?|$)/i.test(u.pathname)) {
            out = Buffer.from(rewriteRefs(body.toString('utf-8'), dirname(localPath)), 'utf-8');
        }

        const fullPath = join(hostDir, localPath);
        await mkdir(dirname(fullPath), { recursive: true });
        await writeFile(fullPath, out);
        assetCount++;
    } catch {}
}

// Calculate relative path from one file's directory to another path
function relPath(fromDir, toPath) {
    const fromParts = fromDir.split('/').filter(Boolean);
    const toParts = toPath.split('/').filter(Boolean);
    
    // Find common prefix length
    let common = 0;
    while (common < fromParts.length && common < toParts.length && fromParts[common] === toParts[common]) {
        common++;
    }
    
    // Build relative path: go up from fromDir, then down to toPath
    const ups = fromParts.length - common;
    const rel = (ups > 0 ? '../'.repeat(ups) : './') + toParts.slice(common).join('/');
    return rel;
}

// Rewrite same-site links/assets to relative paths and strip analytics for offline viewing.
function rewriteHtml(html, pageDir) {
    html = rewriteRefs(html, pageDir);

    // Remove external tracking/analytics scripts that won't work offline
    if (stripAnalytics) {
        html = html.replace(/<script[^>]*src=[^>]*cdn-cgi[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*cloudflareinsights[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*>[^<]*zaraz[^<]*<\/script>/gi, '');
        html = html.replace(/<script[^>]*>[^<]*faro-web-sdk[^<]*<\/script>/gi, '');
        html = html.replace(/<script[^>]*>[^<]*grafana[^<]*<\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*faro[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*google-analytics[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*googletagmanager[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*>[^<]*gtag\([^<]*<\/script>/gi, '');
        html = html.replace(/<script[^>]*>[^<]*google-analytics[^<]*<\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*hotjar[^>]*><\/script>/gi, '');
        html = html.replace(/<script[^>]*src=[^>]*facebook[^>]*fbevents[^>]*><\/script>/gi, '');
        html = html.replace(/<noscript[^>]*>[^<]*facebook[^<]*<\/noscript>/gi, '');
    }
    
    return html;
}

// Collect starting URLs
let startUrls = [startUrl];

if (useSitemap) {
    console.log(`[STATUS] Discovering pages via sitemap...`);
    const sitemapUrls = await discoverFromSitemap();
    
    if (sitemapUrls.length > 0) {
        // Filter by base path
        const filtered = basePath === '/' 
            ? sitemapUrls 
            : sitemapUrls.filter(u => {
                try { return new URL(u).pathname.startsWith(basePath); } 
                catch { return false; }
            });
        console.log(`[STATUS] Found ${sitemapUrls.length} total URLs in sitemap, ${filtered.length} match path ${basePath}`);
        if (filtered.length > 0) startUrls = filtered;
    } else {
        console.log(`[STATUS] No sitemap found, using link discovery instead`);
    }
}

console.log(`[STATUS] Crawling ${startUrls.length} URLs (max depth: ${maxDepth}, base path: ${basePath})`);

const crawlerOptions = {
    maxRequestsPerCrawl: 50000,
    // Profile crawls run against a real, authenticated session, so stay gentle to avoid tripping
    // rate limits / WAF blocks: one page at a time, throttled to a polite requests-per-minute.
    maxConcurrency: useProfile ? 1 : 6,
    maxRequestsPerMinute: useProfile ? 30 : 300,
    // Don't let a single blocked/forbidden page (e.g. an occasional 403) retire the session and
    // abort the whole crawl - we run one real authenticated session and just skip bad pages.
    useSessionPool: false,
    requestHandlerTimeoutSecs: 60,
    navigationTimeoutSecs: 30,
    
    preNavigationHooks: [
        async ({ page }, gotoOptions) => {
            // Navigate with a fast, reliable wait. 'load'/'networkidle' can stall indefinitely
            // on sites with continuous background traffic; 'domcontentloaded' always resolves.
            if (gotoOptions) gotoOptions.waitUntil = 'domcontentloaded';

            // Intercept ALL same-host responses to save assets
            page.on('response', async (response) => {
                try {
                    const url = response.url();
                    const status = response.status();
                    if (status < 200 || status >= 300) return;
                    
                    const u = new URL(url);
                    if (!isSameSite(u.hostname)) return;

                    // Skip HTML pages (those are saved by the requestHandler)
                    const contentType = response.headers()['content-type'] || '';
                    if (contentType.match(/^text\/html/i)) return;
                    
                    const body = await response.body().catch(() => null);
                    if (body) await saveAsset(url, body);
                } catch {}
            });
        }
    ],

    async requestHandler({ page, request, enqueueLinks }) {
        const url = new URL(request.url);
        
        // Only process same-host pages under the base path
        if (url.hostname !== startUrlObj.hostname) return;
        if (basePath !== '/' && !url.pathname.startsWith(basePath)) return;

        // Wait for content to render, but never hang: 'networkidle' can never fire on sites with
        // continuous background requests (analytics, sockets), so bound it and fall back cleanly.
        await page.waitForLoadState('domcontentloaded').catch(() => {});
        await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => {});

        // If the page redirected off-site (typically to a login screen), it can't be saved.
        // Surface it clearly instead of skipping silently, so 'nothing downloaded' is explained.
        try {
            const landed = new URL(page.url());
            if (landed.hostname !== startUrlObj.hostname) {
                console.log(`[ERROR] ${request.url} redirected off-site to ${page.url()} - looks like a login page. Not logged in to this site in the selected browser profile? Sign in first (Open in Browser), then close the browser and retry.`);
                return;
            }
        } catch {}

        if (waitMs > 0) {
            await new Promise(r => setTimeout(r, waitMs));
        }

        // Get rendered HTML
        let html = await page.content();
        
        // Determine save path
        let savePath = url.pathname;
        if (savePath.endsWith('/')) savePath += 'index.html';
        else if (!savePath.match(/\.[a-z0-9]+$/i)) savePath += '/index.html';
        
        // Convert absolute paths to relative for offline viewing
        const pageDir = dirname(savePath);
        html = rewriteHtml(html, pageDir);
        
        const fullPath = join(hostDir, savePath);
        
        // Save HTML file
        await mkdir(dirname(fullPath), { recursive: true });
        await writeFile(fullPath, html, 'utf-8');
        savedCount++;
        console.log(`[SAVED] ${savePath} (${savedCount} pages, ${assetCount} assets)`);

        // Also follow links for discovery (in case sitemap is incomplete)
        await enqueueLinks({
            strategy: 'same-domain',
            transformRequestFunction: (req) => {
                try {
                    const u = new URL(req.url);
                    if (basePath !== '/' && !u.pathname.startsWith(basePath)) return false;
                    if (u.pathname.match(/\.(css|js|png|jpg|jpeg|gif|svg|ico|woff2?|ttf|eot|webp|avif|pdf|zip)$/i)) return false;
                } catch { return false; }
                return req;
            }
        });
    },

    failedRequestHandler({ request, error }) {
        console.log(`[ERROR] ${request.url}: ${error.message}`);
    },
};

// Reuse the real browser profile via a persistent context so the crawl is authenticated.
// channel selects the installed browser (system Chrome/Edge, not bundled Chromium);
// --profile-directory picks the profile within the User Data folder.
if (useProfile) {
    crawlerOptions.launchContext = {
        userDataDir: userDataDir,
        useIncognitoPages: false,
        launchOptions: {
            channel: browserChannel,
            headless: !headful,
            args: ['--profile-directory=' + profileDirectory],
        },
    };
    console.log(`[STATUS] Reusing ${browserChannel} profile at ${userDataDir} (profile: ${profileDirectory}, headful: ${headful})`);
}

const crawler = new PlaywrightCrawler(crawlerOptions);

try {
    await crawler.run(startUrls);
} catch (err) {
    // A launch/close race can throw during shutdown after pages were already saved; that's
    // benign. Only surface a genuine crawl failure.
    if (!isTeardownNoise(err))
        console.log(`[ERROR] Crawl failed: ${(err && err.message) || err}`);
}

if (savedCount === 0) {
    console.log(`[ERROR] No pages were saved. If this site needs a login, sign in to it in the selected browser profile first (use the 'Open in Browser' button, complete the login, then CLOSE the browser) and download again. The start page likely redirected to a login screen.`);
}
console.log(`[DONE] Downloaded ${savedCount} pages + ${assetCount} assets from ${startUrlObj.hostname}`);
process.exit(savedCount > 0 ? 0 : 1);
";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                CancelDownload();
                CleanupProcess();
            }
        }
    }

    /// <summary>
    /// Requirements check result for the Playwright engine.
    /// </summary>
    public class EngineRequirements
    {
        public bool NodeInstalled { get; set; }
        public string NodeVersion { get; set; }
        public bool NpmInstalled { get; set; }
        public bool CrawlerInstalled { get; set; }

        public bool IsReady => NodeInstalled && NpmInstalled && CrawlerInstalled;

        public string GetStatusMessage()
        {
            if (IsReady)
                return $"Ready (Node.js {NodeVersion})";
            if (!NodeInstalled)
                return "Node.js not found. Install from https://nodejs.org/";
            if (!NpmInstalled)
                return "npm not found. Reinstall Node.js.";
            return "Crawler not set up. Click 'Setup' to install.";
        }
    }

    /// <summary>
    /// Simple process execution result.
    /// </summary>
    internal class ProcessResult
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }
}
