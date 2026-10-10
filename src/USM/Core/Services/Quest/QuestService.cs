using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services;
using USM.Core.Services.Localization;

namespace USM.Core.Services.Quest
{
    public sealed record WirelessAdbEndpoint(string Ip, int Port, string ServiceName, string DeviceName);

    public class QuestService
    {
        public const string OriginalPackage = "com.Anotherway.UnpluggedVR";
        public const string ModdedPackage = "com.Anotherway.UnpluggedVR.modded";
        public const string ModdedSongsFolderName = "Songs";
        public static string ModdedSongsPath => $"/sdcard/ModData/{ModdedPackage}/{ModdedSongsFolderName}";
        public const string ModdedAppName = "Unplugged Modded";

        private sealed class QuestSaveBackup
        {
            public string? SavePath { get; init; }
            public bool SafeForCleanReinstall { get; init; }
        }

        private string? _adbPath;
        private string? _apktoolJar;
        private string? _signerJar;
        private string? _lemonPatcherExe;
        private string? _melonDataZip;
        private string? _modDllPath;
        private readonly UpdateService _updateService;

        public QuestService(UpdateService? updateService = null)
        {
            _updateService = updateService ?? new UpdateService();
            FindToolPaths();
        }

        public static string GetQuestToolsCacheDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USM", "tools", "quest");
        }

        private static async Task<string> EnsureStableQuestSigningKeystoreAsync(Action<string>? log)
        {
            string signingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USM", "QuestSigning");
            string stableKeystore = Path.Combine(signingDir, "debug.keystore");
            Directory.CreateDirectory(signingDir);

            if (File.Exists(stableKeystore)) return stableKeystore;

            // Reuse Android's existing per-user debug key when present. On this PC it is
            // the same certificate used by the current modded Quest installation.
            string userDebugKeystore = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".android", "debug.keystore");
            if (File.Exists(userDebugKeystore))
            {
                File.Copy(userDebugKeystore, stableKeystore, overwrite: false);
                log?.Invoke("Saved the existing Android signing key in USM's persistent Quest signing folder.");
                return stableKeystore;
            }

            string keytool = "keytool";
            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                string candidate = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");
                if (File.Exists(candidate)) keytool = candidate;
            }

            if (keytool == "keytool")
            {
                string executable = OperatingSystem.IsWindows() ? "keytool.exe" : "keytool";
                foreach (string pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string candidate = Path.Combine(pathEntry.Trim('"'), executable);
                    if (File.Exists(candidate))
                    {
                        keytool = candidate;
                        break;
                    }
                }
            }

            string arguments = $"-genkeypair -v -keystore \"{stableKeystore}\" -storepass android -alias androiddebugkey -keypass android -keyalg RSA -keysize 2048 -validity 10000 -dname \"CN=USM Quest Signing,O=USM,C=US\" -noprompt";
            var (code, stdout, stderr) = await RunProcessAsync(keytool, arguments, 60000);
            if (code != 0 || !File.Exists(stableKeystore))
            {
                throw new InvalidOperationException($"Could not create a persistent Quest signing key. {stdout} {stderr}".Trim());
            }

            log?.Invoke("Created a persistent Android signing key for future USM Quest updates.");
            return stableKeystore;
        }

        private void FindToolPaths()
        {
            // 1. Locate ADB
            string[] possibleAdb =
            {
                @"D:\platform-tools\adb.exe",
                @"C:\platform-tools\adb.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe")
            };

            foreach (var candidate in possibleAdb)
            {
                if (File.Exists(candidate))
                {
                    _adbPath = candidate;
                    break;
                }
            }

            if (_adbPath == null)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = "adb",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        string? outStr = p.StandardOutput.ReadLine()?.Trim();
                        p.WaitForExit();
                        if (!string.IsNullOrEmpty(outStr) && File.Exists(outStr))
                        {
                            _adbPath = outStr;
                        }
                    }
                }
                catch { }

                if (_adbPath == null)
                {
                    _adbPath = "adb";
                }
            }

            // 2. Locate Tools in AppData cache
            string cacheDir = GetQuestToolsCacheDirectory();
            ResolveToolPaths(cacheDir);

            // If not complete in cache, check if local dev directories have tools to seed cache
            if (!AreToolsReady())
            {
                SeedCacheFromLocalIfAvailable(cacheDir);
                ResolveToolPaths(cacheDir);
            }
        }

        public void ResolveToolPaths(string dir)
        {
            if (!Directory.Exists(dir)) return;

            string apktool = Path.Combine(dir, "apktool.jar");
            string signer = Path.Combine(dir, "uber-apk-signer.jar");
            string lemonPatcher = Path.Combine(dir, "lemon_patcher", "lemon_patcher.exe");
            string melonData = Path.Combine(dir, "melon_data.zip");
            string cachedModDll = Path.Combine(dir, "UnpluggedCustomSongs.dll");

            if (File.Exists(apktool)) _apktoolJar = apktool;
            if (File.Exists(signer)) _signerJar = signer;
            if (File.Exists(lemonPatcher)) _lemonPatcherExe = lemonPatcher;
            if (File.Exists(melonData)) _melonDataZip = melonData;

            // Keep the PC and Quest builds separate. The generic DLL is the PC build;
            // never send it to LemonLoader just because it is beside the USM app.
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string? embeddedQuestMod = PackagedModAssemblyProvider.ExtractQuestModAssembly();
            string[] modCandidates =
            {
                embeddedQuestMod ?? string.Empty,
                Path.Combine(appDir, "UnpluggedCustomSongs.Quest.dll"),
                Path.Combine(appDir, @"..\..\..\..\mod\UnpluggedCustomSongs\bin\Release\net8.0\UnpluggedCustomSongs.Quest.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), @"mod\UnpluggedCustomSongs\bin\Release\net8.0\UnpluggedCustomSongs.Quest.dll"),
                Path.Combine(appDir, "UnpluggedCustomSongs.dll"),
                cachedModDll
            };
            _modDllPath = modCandidates.FirstOrDefault(candidate => File.Exists(candidate) && IsQuestModAssembly(candidate));
        }

        private static bool IsQuestModAssembly(string path)
        {
            try
            {
                return string.Equals(AssemblyName.GetAssemblyName(path).Name, "UnpluggedCustomSongs.Quest", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public bool AreToolsReady()
        {
            return !string.IsNullOrEmpty(_apktoolJar) && File.Exists(_apktoolJar) &&
                   !string.IsNullOrEmpty(_signerJar) && File.Exists(_signerJar) &&
                   !string.IsNullOrEmpty(_lemonPatcherExe) && File.Exists(_lemonPatcherExe) &&
                   !string.IsNullOrEmpty(_melonDataZip) && File.Exists(_melonDataZip) &&
                   !string.IsNullOrEmpty(_modDllPath) && File.Exists(_modDllPath);
        }

        private void SeedCacheFromLocalIfAvailable(string cacheDir)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] searchDirs =
                {
                    Path.Combine(baseDir, "tools", "quest"),
                    Path.Combine(baseDir, @"..\..\..\..\tools\quest"),
                    Path.Combine(Directory.GetCurrentDirectory(), "tools", "quest")
                };

                foreach (var dir in searchDirs)
                {
                    if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "apktool.jar")))
                    {
                        Directory.CreateDirectory(cacheDir);
                        foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(dir, file);
                            string target = Path.Combine(cacheDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            if (!File.Exists(target))
                            {
                                File.Copy(file, target, true);
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        public async Task<(bool Success, string Message)> EnsureToolsReadyAsync(
            IProgress<(double Progress, string Status)>? progress = null,
            Action<string>? log = null)
        {
            string cacheDir = GetQuestToolsCacheDirectory();
            ResolveToolPaths(cacheDir);

            if (AreToolsReady())
            {
                return (true, LocalizationService.T("Log_QuestToolsCached", cacheDir));
            }

            SeedCacheFromLocalIfAvailable(cacheDir);
            ResolveToolPaths(cacheDir);

            if (AreToolsReady())
            {
                return (true, LocalizationService.T("Log_QuestToolsCached", cacheDir));
            }

            log?.Invoke(LocalizationService.T("Log_QuestToolsDownloading", 0));
            var (dlSuccess, dlMsg) = await _updateService.DownloadAndCacheQuestToolsAsync(cacheDir, null, progress);
            if (!dlSuccess)
            {
                log?.Invoke(LocalizationService.T("Log_QuestToolsFailed", dlMsg));
                return (false, dlMsg);
            }

            ResolveToolPaths(cacheDir);
            if (AreToolsReady())
            {
                log?.Invoke(LocalizationService.T("Log_QuestToolsCached", cacheDir));
                return (true, dlMsg);
            }

            return (false, "Some Quest tools components are missing after download.");
        }

        public string GetAdbPath() => _adbPath ?? "adb";
        public string? GetApktoolJar() => _apktoolJar;
        public string? GetSignerJar() => _signerJar;
        public string? GetLemonPatcherExe() => _lemonPatcherExe;
        public string? GetMelonDataZip() => _melonDataZip;
        public string? GetModDllPath() => _modDllPath;

        public async Task<List<QuestDevice>> GetDevicesAsync()
        {
            var result = new List<QuestDevice>();
            var (exitCode, stdout, _) = await RunProcessAsync(GetAdbPath(), "devices -l");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return result;

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string serial = parts[0];
                    string status = parts[1];
                    string model = "Meta Quest";

                    foreach (var p in parts.Skip(2))
                    {
                        if (p.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                        {
                            model = p.Substring("model:".Length).Replace("_", " ");
                            break;
                        }
                    }

                    if (status != "device")
                    {
                        model += $" ({status.ToUpper()})";
                    }

                    result.Add(new QuestDevice
                    {
                        Serial = serial,
                        Status = status,
                        Model = model
                    });
                }
            }
            return result;
        }

        public async Task<WirelessAdbEndpoint?> DiscoverWirelessAdbAsync(Action<string>? log = null, int attempts = 2)
        {
            int maxAttempts = Math.Clamp(attempts, 1, 2);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                log?.Invoke($"Searching for wireless ADB through mDNS ({attempt}/{maxAttempts})...");

                var (_, snapshot, _) = await RunProcessAsync(GetAdbPath(), "mdns services", 3000);
                WirelessAdbEndpoint? endpoint = ParseWirelessAdbEndpoint(snapshot);

                if (endpoint == null)
                {
                    // Newer platform-tools expose IP, port, and device model in a stream.
                    // Capture a short window, then stop the long-running tracker.
                    var (_, tracked, _) = await RunProcessForWindowAsync(
                        GetAdbPath(), "mdns track-services --proto-text", 1400);
                    endpoint = ParseWirelessAdbEndpoint(tracked);
                }

                if (endpoint != null)
                {
                    log?.Invoke($"mDNS found {endpoint.DeviceName} at {endpoint.Ip}:{endpoint.Port} ({endpoint.ServiceName}).");
                    return endpoint;
                }

                if (attempt < maxAttempts) await Task.Delay(800);
            }

            log?.Invoke("mDNS did not find a connectable wireless ADB service.");
            return null;
        }

        private static WirelessAdbEndpoint? ParseWirelessAdbEndpoint(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var candidates = new List<WirelessAdbEndpoint>();
            foreach (Match blockMatch in Regex.Matches(output, @"\bservice\s*\{(?<body>.*?)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string body = blockMatch.Groups["body"].Value;
                string serviceName = ReadProtoString(body, "service");
                if (!IsConnectableAdbService(serviceName)) continue;

                string ip = ReadProtoString(body, "ipv4");
                int port = ReadProtoInt(body, "port");
                if (!IsValidIpv4(ip) || port is < 1 or > 65535) continue;

                string deviceName = ReadProtoString(body, "product_model");
                if (string.IsNullOrWhiteSpace(deviceName)) deviceName = ReadProtoString(body, "given_name");
                if (string.IsNullOrWhiteSpace(deviceName)) deviceName = ReadProtoString(body, "instance");
                candidates.Add(new WirelessAdbEndpoint(ip, port, serviceName, deviceName));
            }

            if (candidates.Count == 0)
            {
                // Older platform-tools return a one-line service list instead of proto text.
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string serviceName = line.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
                        ? "_adb-tls-connect._tcp"
                        : line.Contains("_adb._tcp", StringComparison.OrdinalIgnoreCase) &&
                          !line.Contains("_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase)
                            ? "_adb._tcp"
                            : string.Empty;
                    if (serviceName.Length == 0) continue;

                    Match address = Regex.Match(line, @"(?<ip>(?:\d{1,3}\.){3}\d{1,3})[:\s]+(?<port>\d{1,5})");
                    if (!address.Success) continue;

                    string ip = address.Groups["ip"].Value;
                    if (!IsValidIpv4(ip) || !int.TryParse(address.Groups["port"].Value, out int port) || port is < 1 or > 65535)
                        continue;

                    candidates.Add(new WirelessAdbEndpoint(ip, port, serviceName, "ADB device"));
                }
            }

            if (candidates.Count == 1) return candidates[0];

            var questCandidates = candidates
                .Where(candidate => candidate.DeviceName.Contains("Quest", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return questCandidates.Count == 1 ? questCandidates[0] : null;
        }

        private static bool IsConnectableAdbService(string serviceName) =>
            serviceName.Equals("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Equals("_adb._tcp", StringComparison.OrdinalIgnoreCase);

        private static string ReadProtoString(string block, string property)
        {
            Match match = Regex.Match(block, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*""(?<value>[^""]*)""");
            return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
        }

        private static int ReadProtoInt(string block, string property)
        {
            Match match = Regex.Match(block, $@"(?m)^\s*{Regex.Escape(property)}\s*:\s*(?<value>\d+)");
            return match.Success && int.TryParse(match.Groups["value"].Value, out int value) ? value : 0;
        }

        private static bool IsValidIpv4(string value) =>
            IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;

        public async Task<(bool Success, string Message)> ConnectWirelessAsync(string ip, int port = 5555)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return (false, "IP address cannot be empty.");

            string endpoint = ip.Contains(":") ? ip : $"{ip}:{port}";
            var (_, stdout, stderr) = await RunProcessAsync(GetAdbPath(), $"connect {endpoint}", 10000);
            
            string output = (stdout + " " + stderr).Trim();
            if (output.Contains("connected", StringComparison.OrdinalIgnoreCase) && !output.Contains("unable", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"Connected to {endpoint}");
            }
            return (false, output);
        }

        public async Task<bool> IsPackageInstalledAsync(string serial, string packageName)
        {
            var (exitCode, stdout, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm list packages");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return false;

            return stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Select(l => l.StartsWith("package:") ? l.Substring("package:".Length).Trim() : l)
                         .Any(p => p.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<string?> GetPackageApkPathAsync(string serial, string packageName)
        {
            string actualPackage = packageName;
            var (listCode, listOut, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm list packages");
            if (listCode == 0 && !string.IsNullOrWhiteSpace(listOut))
            {
                var matchPkg = listOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(l => l.Trim())
                                      .Select(l => l.StartsWith("package:") ? l.Substring("package:".Length).Trim() : l)
                                      .FirstOrDefault(p => p.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                if (matchPkg != null)
                {
                    actualPackage = matchPkg;
                }
            }

            var (exitCode, stdout, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm path {actualPackage}");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            var match = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault(l => l.Trim().StartsWith("package:"));
            return match != null ? match.Substring("package:".Length).Trim() : null;
        }

        public async Task<bool> PullApkAsync(string serial, string remotePath, string localPath, Action<string>? log = null)
        {
            log?.Invoke(LocalizationService.T("Log_PullingApk", remotePath, localPath));
            var (code, outStr, errStr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} pull \"{remotePath}\" \"{localPath}\"", 300000);
            if (code == 0 && File.Exists(localPath))
            {
                log?.Invoke(LocalizationService.T("Log_ApkPulledSuccess", $"{new FileInfo(localPath).Length / (1024 * 1024)}"));
                return true;
            }
            log?.Invoke(LocalizationService.T("Log_ApkPulledFail", $"{outStr} {errStr}".Trim()));
            return false;
        }

        public async Task<(bool Success, string Message)> InstallApkAsync(
            string serial,
            string localApkPath,
            Action<string>? log = null,
            bool safeToUninstallExisting = false)
        {
            log?.Invoke(LocalizationService.T("Log_InstallingApk", ModdedPackage));

            // Clean any stale temp APKs from /data/local/tmp on Quest to maximize free space
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell \"rm -f /data/local/tmp/*.apk\"");

            // Try install with --no-incremental to bypass adb incremental service storage restriction
            var (code, outStr, errStr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} install --no-incremental -r -d \"{localApkPath}\"", 300000);
            string full = (outStr + " " + errStr).Trim();
            bool success = full.Contains("Success", StringComparison.OrdinalIgnoreCase);

            // A normal update must keep the app data. Only use the clean reinstall
            // fallback if the signatures truly differ and a verified save backup exists.
            if (!success && full.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase))
            {
                if (!safeToUninstallExisting)
                {
                    const string reason = "The installed app has a different signature, and its save could not be backed up. It was left installed to protect your data.";
                    log?.Invoke(reason);
                    return (false, reason);
                }

                log?.Invoke(LocalizationService.T("Log_SignatureMismatchUninstall"));
                // `uninstall -k` is not supported by the Quest package manager. The
                // save was already pulled and validated, so fully uninstall and restore it.
                var (uninstallCode, uninstallOut, uninstallErr) = await RunProcessAsync(
                    GetAdbPath(), $"-s {serial} uninstall {ModdedPackage}");
                if (uninstallCode != 0 || !(uninstallOut + uninstallErr).Contains("Success", StringComparison.OrdinalIgnoreCase))
                {
                    string uninstallMessage = (uninstallOut + " " + uninstallErr).Trim();
                    log?.Invoke($"Could not safely remove the old app: {uninstallMessage}");
                    return (false, uninstallMessage);
                }

                var (cRetry, oRetry, eRetry) = await RunProcessAsync(GetAdbPath(), $"-s {serial} install --no-incremental -d \"{localApkPath}\"", 300000);
                full = (oRetry + " " + eRetry).Trim();
                success = full.Contains("Success", StringComparison.OrdinalIgnoreCase);
            }

            if (!success && !full.Contains("INSTALL_FAILED_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback to standard install in case the ADB version doesn't support --no-incremental
                var (code2, outStr2, errStr2) = await RunProcessAsync(GetAdbPath(), $"-s {serial} install -r -d \"{localApkPath}\"", 300000);
                full = (outStr2 + " " + errStr2).Trim();
                success = full.Contains("Success", StringComparison.OrdinalIgnoreCase);
            }

            if (success)
            {
                log?.Invoke(LocalizationService.T("Log_InstallSuccess"));
                return (true, "Installation successful!");
            }
            log?.Invoke($"Installation error: {full}");
            return (false, full);
        }

        public async Task GrantStoragePermissionsAsync(string serial, string packageName, Action<string>? log = null)
        {
            log?.Invoke(LocalizationService.T("Log_GrantingPermissions"));
            var (appOpsCode, _, appOpsError) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell appops set --uid {packageName} MANAGE_EXTERNAL_STORAGE allow");
            if (appOpsCode != 0)
                log?.Invoke($"Warning: Could not grant all-files access for ModData: {appOpsError}");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.READ_EXTERNAL_STORAGE");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.WRITE_EXTERNAL_STORAGE");

            // Setup songs directories
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/ModData/{packageName}/Songs");
            log?.Invoke(LocalizationService.T("Log_PermissionsReady"));
        }

        public async Task<string?> GetPackageVersionAsync(string serial, string packageName)
        {
            var (exitCode, stdout, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell dumpsys package {packageName}");
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var match = Regex.Match(stdout, @"versionName=([^\s\r\n]+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            return null;
        }

        public async Task<(bool Success, string Message)> UninstallModdedPackageAsync(string serial, Action<string>? log = null)
        {
            var saveBackup = await BackupQuestSaveDataAsync(serial, log);
            if (!saveBackup.SafeForCleanReinstall)
            {
                const string backupError = "The Quest save could not be backed up, so the modded app was left installed.";
                log?.Invoke(backupError);
                return (false, backupError);
            }

            log?.Invoke(LocalizationService.T("Log_Uninstalling", ModdedPackage));
            var (exitCode, stdout, stderr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} uninstall {ModdedPackage}");
            string output = (stdout + " " + stderr).Trim();
            bool success = output.Contains("Success", StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                log?.Invoke(LocalizationService.T("Log_UninstallSuccess"));
                // Clean up OBB and app data
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell rm -rf /sdcard/Android/obb/{ModdedPackage}");
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell rm -rf /sdcard/Android/data/{ModdedPackage}");
                if (!string.IsNullOrEmpty(saveBackup.SavePath))
                {
                    log?.Invoke($"Quest save backup retained on this PC: {saveBackup.SavePath}");
                }
                return (true, "Uninstalled successfully.");
            }
            log?.Invoke($"Failed to uninstall: {output}");
            return (false, output);
        }

        public static string GenerateUnlockedSavePayload()
        {
            return TryCreateUnlockedSaveData(null, out var payload)
                ? payload
                : throw new InvalidOperationException("Could not create the unlocked save payload.");
        }

        private static string EncryptSaveData(string key, string plainText)
        {
            byte[] iv = new byte[16];
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = System.Text.Encoding.UTF8.GetBytes(key);
            aes.IV = iv;
            using var transform = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();
            using (var cs = new System.Security.Cryptography.CryptoStream(ms, transform, System.Security.Cryptography.CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plainText);
            }
            return Convert.ToBase64String(ms.ToArray());
        }

        private const string SaveEncryptionKey = "b14ca5898a4e4133bbce2ea2315a1916";

        private static string DecryptSaveData(string encryptedText)
        {
            byte[] encrypted = Convert.FromBase64String(encryptedText.Trim());
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = System.Text.Encoding.UTF8.GetBytes(SaveEncryptionKey);
            aes.IV = new byte[16];
            using var transform = aes.CreateDecryptor(aes.Key, aes.IV);
            using var input = new MemoryStream(encrypted);
            using var crypto = new System.Security.Cryptography.CryptoStream(input, transform, System.Security.Cryptography.CryptoStreamMode.Read);
            using var reader = new StreamReader(crypto);
            return reader.ReadToEnd();
        }

        private static bool IsValidEncryptedSaveData(string? encryptedText)
        {
            if (string.IsNullOrWhiteSpace(encryptedText)) return false;
            try
            {
                return JsonNode.Parse(DecryptSaveData(encryptedText)) is JsonObject root
                    && root["SavedData"] is JsonObject savedData
                    && savedData.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateUnlockedSaveData(string? encryptedSave, out string updatedSave)
        {
            updatedSave = string.Empty;
            try
            {
                JsonObject root;
                if (string.IsNullOrWhiteSpace(encryptedSave))
                {
                    root = new JsonObject();
                }
                else
                {
                    root = JsonNode.Parse(DecryptSaveData(encryptedSave)) as JsonObject
                        ?? throw new FormatException("The save data is not a JSON object.");
                }

                if (root["SavedData"] is not JsonObject savedData)
                {
                    savedData = new JsonObject();
                    root["SavedData"] = savedData;
                }

                // This option is explicitly enabled by the user. Set the tutorial flag
                // even when an existing save is present; previously it only applied to
                // a brand-new save, which made the checked option appear ineffective.
                SetWrappedValue(savedData, "HasDoneTutorial", "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", "true");
                SetWrappedValue(savedData, "ShowIntro", "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", "false");

                var completed = ReadStringList(savedData["AlbumCompleted"]);
                for (int i = 1; i <= 5; i++)
                {
                    string album = $"CampaignAlbum{i}";
                    if (!completed.Contains(album, StringComparer.Ordinal)) completed.Add(album);
                }
                SetWrappedValue(savedData, "AlbumCompleted", "System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", JsonSerializer.Serialize(completed));

                updatedSave = EncryptSaveData(SaveEncryptionKey, root.ToJsonString());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> ReadStringList(JsonNode? value)
        {
            if (value is JsonObject wrapper) value = wrapper["JsonValue"];
            if (value is JsonArray array)
            {
                return array.Select(item => item?.GetValue<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var serialized) && !string.IsNullOrWhiteSpace(serialized))
            {
                try
                {
                    using var document = JsonDocument.Parse(serialized);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return document.RootElement.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!)
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                    }
                }
                catch { }
            }
            return new List<string>();
        }

        private static void SetWrappedValue(JsonObject savedData, string key, string type, string value)
        {
            if (savedData[key] is not JsonObject wrapper)
            {
                wrapper = new JsonObject();
                savedData[key] = wrapper;
            }

            wrapper["Type"] = type;
            wrapper["JsonValue"] = value;
        }

        private async Task<QuestSaveBackup> BackupQuestSaveDataAsync(string serial, Action<string>? log)
        {
            string serialName = string.Concat(serial.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USM", "QuestSaveBackups", serialName,
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));

            var candidates = new[]
            {
                (ModdedPackage, $"/sdcard/Android/data/{ModdedPackage}/files/SavedData.json"),
                (ModdedPackage, $"/sdcard/ModData/{ModdedPackage}/files/SavedData.json"),
                (OriginalPackage, $"/sdcard/Android/data/{OriginalPackage}/files/SavedData.json"),
                (OriginalPackage, $"/sdcard/ModData/{OriginalPackage}/files/SavedData.json")
            };

            foreach (var (package, remotePath) in candidates)
            {
                var (listCode, listOut, listErr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell ls -l \"{remotePath}\"", 15000);
                string listing = (listOut + " " + listErr).Trim();
                if (listing.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)) continue;
                if (listCode != 0)
                {
                    log?.Invoke($"Could not check Quest save at {remotePath}: {listing}");
                    return new QuestSaveBackup { SafeForCleanReinstall = false };
                }

                try
                {
                    Directory.CreateDirectory(backupDir);
                    string backupPath = Path.Combine(backupDir, $"{package}-SavedData.json");
                    var (pullCode, pullOut, pullErr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} pull \"{remotePath}\" \"{backupPath}\"", 30000);
                    if (pullCode != 0 || !File.Exists(backupPath))
                    {
                        string pullMessage = (pullOut + " " + pullErr).Trim();
                        log?.Invoke($"Could not back up the Quest save at {remotePath}: {pullMessage}");
                        return new QuestSaveBackup { SafeForCleanReinstall = false };
                    }

                    string backedUpSave = await File.ReadAllTextAsync(backupPath);
                    if (!IsValidEncryptedSaveData(backedUpSave))
                    {
                        log?.Invoke($"A save file exists at {remotePath}, but its encrypted data could not be validated. It was retained at {backupPath}; USM will not uninstall the app or overwrite this save.");
                        continue;
                    }

                    log?.Invoke($"Quest save backed up from {package} to {backupPath}.");
                    return new QuestSaveBackup { SavePath = backupPath, SafeForCleanReinstall = true };
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Could not save a local Quest backup: {ex.Message}");
                    return new QuestSaveBackup { SafeForCleanReinstall = false };
                }
            }

            log?.Invoke("No valid Quest SavedData.json could be backed up. The app will be left installed if a signature conflict would require removing its data.");
            return new QuestSaveBackup { SafeForCleanReinstall = false };
        }

        private async Task<bool> RestoreQuestSaveDataAsync(string serial, QuestSaveBackup backup, Action<string>? log)
        {
            if (string.IsNullOrEmpty(backup.SavePath)) return true;
            if (!File.Exists(backup.SavePath))
            {
                log?.Invoke($"The local Quest save backup is missing: {backup.SavePath}");
                return false;
            }
            if (!IsValidEncryptedSaveData(await File.ReadAllTextAsync(backup.SavePath)))
            {
                log?.Invoke("The local Quest save backup failed validation, so USM did not push it back to the headset.");
                return false;
            }

            var targets = new[]
            {
                $"/sdcard/Android/data/{ModdedPackage}/files",
                $"/sdcard/ModData/{ModdedPackage}/files"
            };
            bool restored = false;
            foreach (var target in targets)
            {
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p \"{target}\"");
                var (pushCode, pushOut, pushErr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{backup.SavePath}\" \"{target}/SavedData.json\"", 30000);
                if (pushCode == 0) restored = true;
                else log?.Invoke($"Could not restore the backed up save to {target}: {(pushOut + " " + pushErr).Trim()}");
            }

            if (restored) log?.Invoke("The existing Quest save was restored to the modded app.");
            else log?.Invoke("The APK is installed, but USM could not restore the save. The validated backup remains on this PC.");
            return restored;
        }

        private async Task SetupUnlockedSaveDataAsync(string serial, QuestSaveBackup backup, Action<string>? log = null)
        {
            try
            {
                log?.Invoke(LocalizationService.T("Log_InjectingSave"));
                if (!backup.SafeForCleanReinstall && string.IsNullOrEmpty(backup.SavePath))
                {
                    log?.Invoke("The Quest save could not be read safely, so the intro and campaign flags were left unchanged.");
                    return;
                }

                string? existingSave = !string.IsNullOrEmpty(backup.SavePath) && File.Exists(backup.SavePath)
                    ? await File.ReadAllTextAsync(backup.SavePath)
                    : null;
                if (!TryCreateUnlockedSaveData(existingSave, out string encryptedData))
                {
                    log?.Invoke("Could not decode the existing Quest save. It was left unchanged to protect progress.");
                    return;
                }

                string tempFile = Path.Combine(Path.GetTempPath(), $"USM-SavedData-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(tempFile, encryptedData);

                try
                {
                    bool saved = false;
                    foreach (var target in new[]
                    {
                        $"/sdcard/Android/data/{ModdedPackage}/files",
                        $"/sdcard/ModData/{ModdedPackage}/files"
                    })
                    {
                        await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p \"{target}\"");
                        var (pushCode, pushOut, pushErr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{tempFile}\" \"{target}/SavedData.json\"", 30000);
                        if (pushCode == 0) saved = true;
                        else log?.Invoke($"Could not write the updated save to {target}: {(pushOut + " " + pushErr).Trim()}");
                    }

                    if (saved) log?.Invoke(LocalizationService.T("Log_SaveSuccess"));
                }
                finally
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Warning: Could not inject unlocked save data: {ex.Message}");
            }
        }

        private static bool TryMergePcSaveIntoQuestSave(string encryptedPcSave, string encryptedQuestSave, out string mergedEncryptedSave, out string description)
        {
            mergedEncryptedSave = string.Empty;
            description = string.Empty;
            try
            {
                JsonObject pcRoot = JsonNode.Parse(DecryptSaveData(encryptedPcSave)) as JsonObject
                    ?? throw new FormatException("The PC save root is not a JSON object.");
                JsonObject questRoot = JsonNode.Parse(DecryptSaveData(encryptedQuestSave)) as JsonObject
                    ?? throw new FormatException("The Quest save root is not a JSON object.");
                JsonObject pcData = pcRoot["SavedData"] as JsonObject
                    ?? throw new FormatException("The PC save does not contain SavedData.");
                JsonObject questData = questRoot["SavedData"] as JsonObject
                    ?? throw new FormatException("The Quest save does not contain SavedData.");

                var copied = new List<string>();
                foreach (string key in new[] { "HasDoneTutorial", "ShowIntro", "SelectedAlbumID", "AlbumCompleted" })
                {
                    if (!TryMergeCompatibleWrappedField(pcData, questData, key)) continue;
                    copied.Add(key);
                }

                if (copied.Count == 0)
                {
                    description = "No compatible PC progress fields were found in the current Quest save. No Quest data was changed.";
                    return false;
                }

                mergedEncryptedSave = EncryptSaveData(SaveEncryptionKey, questRoot.ToJsonString());
                if (!IsValidEncryptedSaveData(mergedEncryptedSave))
                {
                    mergedEncryptedSave = string.Empty;
                    description = "The merged Quest save failed validation. No Quest data was changed.";
                    return false;
                }

                description = LocalizationService.T("Quest_ImportPcSaveResult", copied.Count);
                return true;
            }
            catch (Exception ex)
            {
                description = $"Could not parse and convert both save files: {ex.Message}";
                return false;
            }
        }

        private static bool TryMergeCompatibleWrappedField(JsonObject source, JsonObject target, string key)
        {
            if (source[key] is not JsonObject sourceWrapper || target[key] is not JsonObject targetWrapper)
                return false;

            string? sourceType = sourceWrapper["Type"]?.GetValue<string>();
            string? targetType = targetWrapper["Type"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sourceType) || !string.Equals(sourceType, targetType, StringComparison.Ordinal))
                return false;

            if (sourceWrapper["JsonValue"] is not JsonValue sourceJsonValue ||
                !sourceJsonValue.TryGetValue<string>(out string? sourceValue) || string.IsNullOrWhiteSpace(sourceValue))
                return false;

            if (key is "HasDoneTutorial" or "ShowIntro")
            {
                bool sourceBool;
                try
                {
                    using var sourceDocument = JsonDocument.Parse(sourceValue);
                    if (sourceDocument.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    sourceBool = sourceDocument.RootElement.GetBoolean();
                }
                catch { return false; }

                bool targetBool = false;
                if (targetWrapper["JsonValue"] is JsonValue targetJsonValue &&
                    targetJsonValue.TryGetValue<string>(out string? targetValue) && !string.IsNullOrWhiteSpace(targetValue))
                {
                    try
                    {
                        using var targetDocument = JsonDocument.Parse(targetValue);
                        if (targetDocument.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            targetBool = targetDocument.RootElement.GetBoolean();
                    }
                    catch { }
                }

                // Keep a completed tutorial completed and a previously dismissed intro dismissed.
                bool merged = key == "HasDoneTutorial" ? sourceBool || targetBool : sourceBool && targetBool;
                targetWrapper["JsonValue"] = merged ? "true" : "false";
                return true;
            }

            if (key == "AlbumCompleted" && targetWrapper["JsonValue"] is JsonValue existingListValue &&
                existingListValue.TryGetValue<string>(out string? existingListJson))
            {
                try
                {
                    var sourceItems = JsonSerializer.Deserialize<List<string>>(sourceValue) ?? new List<string>();
                    var targetItems = JsonSerializer.Deserialize<List<string>>(existingListJson ?? "[]") ?? new List<string>();
                    foreach (string item in sourceItems)
                    {
                        if (!targetItems.Contains(item, StringComparer.Ordinal)) targetItems.Add(item);
                    }
                    targetWrapper["JsonValue"] = JsonSerializer.Serialize(targetItems);
                    return true;
                }
                catch { return false; }
            }

            // SelectedAlbumID is a shared string field. Keep the Quest wrapper/type intact.
            try
            {
                using var document = JsonDocument.Parse(sourceValue);
                if (document.RootElement.ValueKind != JsonValueKind.String) return false;
                targetWrapper["JsonValue"] = sourceValue;
                return true;
            }
            catch { return false; }
        }

        public async Task<(bool Success, string Message)> ImportPcSaveToQuestAsync(string serial, string pcSavePath, Action<string>? log = null)
        {
            if (!File.Exists(pcSavePath)) return (false, "The selected PC save file does not exist.");

            string pcEncryptedSave = await File.ReadAllTextAsync(pcSavePath);
            if (!IsValidEncryptedSaveData(pcEncryptedSave))
                return (false, "The selected file is not a valid encrypted Unplugged PC save. The Quest save was not changed.");

            // Keep a validated local copy of the current Quest save before converting.
            QuestSaveBackup backup = await BackupQuestSaveDataAsync(serial, log);
            if (string.IsNullOrWhiteSpace(backup.SavePath) || !backup.SafeForCleanReinstall || !File.Exists(backup.SavePath))
                return (false, "USM could not validate and back up a current Quest save. The Quest was not changed.");

            string questEncryptedSave = await File.ReadAllTextAsync(backup.SavePath);
            if (!TryMergePcSaveIntoQuestSave(pcEncryptedSave, questEncryptedSave, out string mergedSave, out string description))
                return (false, description);

            string tempPath = Path.Combine(Path.GetTempPath(), $"USM-PC-to-Quest-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPath, mergedSave);
            try
            {
                bool pushed = false;
                foreach (string target in new[]
                {
                    $"/sdcard/ModData/{ModdedPackage}/files",
                    $"/sdcard/Android/data/{ModdedPackage}/files"
                })
                {
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p \"{target}\"");
                    var (pushCode, pushOut, pushErr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{tempPath}\" \"{target}/SavedData.json\"", 30000);
                    if (pushCode == 0)
                    {
                        pushed = true;
                    }
                    else
                    {
                        log?.Invoke($"Could not write converted save to {target}: {(pushOut + " " + pushErr).Trim()}");
                    }
                }

                if (!pushed) return (false, "The converted save was prepared and backed up, but could not be copied to the modded Quest app.");
                log?.Invoke(description);
                log?.Invoke($"PC save merged into the modded Quest save. Original Quest backup: {backup.SavePath}");
                return (true, description);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        public async Task<bool> PatchAndDeployAsync(
            string? serial,
            string? customSourceApk,
            bool skipIntroAndUnlockCampaign,
            IProgress<(int Percent, string Status)> progress,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string workDir = Path.Combine(Path.GetTempPath(), "USM_Quest_Patch");
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, true); } catch { }
            }
            Directory.CreateDirectory(workDir);

            if (!AreToolsReady())
            {
                progress.Report((2, LocalizationService.T("Log_QuestToolsDownloading", 0)));
                var (toolsReady, toolsMsg) = await EnsureToolsReadyAsync(
                    new Progress<(double Progress, string Status)>(p => progress.Report(((int)(p.Progress * 0.05), p.Status))),
                    log);

                if (!toolsReady)
                {
                    log($"ERROR: {toolsMsg}");
                    return false;
                }
            }

            progress.Report((5, LocalizationService.T("Log_VerifyTools")));
            log(LocalizationService.T("Log_StartingPipeline"));

            if (string.IsNullOrEmpty(_apktoolJar) || !File.Exists(_apktoolJar))
            {
                log($"ERROR: apktool.jar not found! Please check {GetQuestToolsCacheDirectory()}/apktool.jar");
                return false;
            }
            if (string.IsNullOrEmpty(_signerJar) || !File.Exists(_signerJar))
            {
                log($"ERROR: uber-apk-signer.jar not found! Please check {GetQuestToolsCacheDirectory()}/uber-apk-signer.jar");
                return false;
            }

            string sourceApk = Path.Combine(workDir, "original.apk");

            // Step 1: Obtain base APK
            if (!string.IsNullOrEmpty(customSourceApk) && File.Exists(customSourceApk))
            {
                progress.Report((10, "Using custom source APK..."));
                log($"Using local source APK: {customSourceApk}");
                File.Copy(customSourceApk, sourceApk, true);
            }
            else
            {
                if (string.IsNullOrEmpty(serial))
                {
                    log("ERROR: No Quest device selected to extract APK.");
                    return false;
                }

                progress.Report((15, "Checking Unplugged on Quest..."));
                log(LocalizationService.T("Log_QueryingPackage", OriginalPackage, serial));

                string? remotePath = await GetPackageApkPathAsync(serial, OriginalPackage);
                if (string.IsNullOrEmpty(remotePath))
                {
                    log(LocalizationService.T("Log_CheckingModdedPackage", OriginalPackage));
                    remotePath = await GetPackageApkPathAsync(serial, ModdedPackage);
                }

                if (string.IsNullOrEmpty(remotePath))
                {
                    log(LocalizationService.T("Log_OriginalNotFound"));
                    return false;
                }

                progress.Report((25, "Pulling APK from Quest..."));
                bool pulled = await PullApkAsync(serial, remotePath, sourceApk, log);
                if (!pulled) return false;
            }

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 2: Decompile APK
            progress.Report((40, "Decompiling APK with Apktool..."));
            log(LocalizationService.T("Log_DecompilingApk"));
            string decompiledDir = Path.Combine(workDir, "decompiled");
            var (dCode, dOut, dErr) = await RunProcessAsync("java", $"-jar \"{_apktoolJar}\" d -f -o \"{decompiledDir}\" \"{sourceApk}\"", 300000);
            if (dCode != 0)
            {
                log($"Decompilation error: {dOut} {dErr}");
                return false;
            }
            log(LocalizationService.T("Log_DecompileSuccess"));

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 3: Patch AndroidManifest and clone package
            progress.Report((60, "Patching Manifest and package identity..."));
            log(LocalizationService.T("Log_PatchingManifest", ModdedPackage));
            string manifestPath = Path.Combine(decompiledDir, "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                log("ERROR: AndroidManifest.xml missing from decompiled APK.");
                return false;
            }

            string manifest = await File.ReadAllTextAsync(manifestPath);
            manifest = Regex.Replace(manifest, @"package=""[^""]*""", $"package=\"{ModdedPackage}\"");
            manifest = Regex.Replace(manifest, @"android:authorities=""[^""]*""", $"android:authorities=\"{ModdedPackage}\"");

            // Add permissions
            string permissions = "\n    <uses-permission android:name=\"android.permission.READ_EXTERNAL_STORAGE\" />" +
                                 "\n    <uses-permission android:name=\"android.permission.WRITE_EXTERNAL_STORAGE\" />" +
                                 "\n    <uses-permission android:name=\"android.permission.MANAGE_EXTERNAL_STORAGE\" />";
            
            var manifestTagMatch = Regex.Match(manifest, @"<manifest[^>]*>");
            if (manifestTagMatch.Success)
            {
                manifest = manifest.Insert(manifestTagMatch.Index + manifestTagMatch.Length, permissions);
            }

            // Legacy storage and flags in application tag
            manifest = Regex.Replace(manifest, @"<application([^>]*)>", m =>
            {
                string attrs = m.Groups[1].Value;
                if (!attrs.Contains("requestLegacyExternalStorage"))
                    attrs += " android:requestLegacyExternalStorage=\"true\"";
                if (!attrs.Contains("extractNativeLibs"))
                    attrs += " android:extractNativeLibs=\"true\"";
                return $"<application{attrs}>";
            });

            await File.WriteAllTextAsync(manifestPath, manifest);
            log(LocalizationService.T("Log_ManifestSuccess"));

            // Update app title in strings.xml
            string stringsXml = Path.Combine(decompiledDir, "res", "values", "strings.xml");
            if (File.Exists(stringsXml))
            {
                string sContent = await File.ReadAllTextAsync(stringsXml);
                sContent = Regex.Replace(sContent, @"<string name=""app_name"">.*?</string>", $"<string name=\"app_name\">{ModdedAppName}</string>");
                await File.WriteAllTextAsync(stringsXml, sContent);
                log($"Updated app title to '{ModdedAppName}' in strings.xml.");
            }

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 4: Rebuild APK
            progress.Report((75, "Rebuilding APK with Apktool..."));
            log(LocalizationService.T("Log_RebuildingApk"));
            string unsignedApk = Path.Combine(workDir, "unplugged_modded_unsigned.apk");
            var (bCode, bOut, bErr) = await RunProcessAsync("java", $"-jar \"{_apktoolJar}\" b \"{decompiledDir}\" -o \"{unsignedApk}\"", 300000);
            if (bCode != 0)
            {
                log($"Rebuild error: {bOut} {bErr}");
                return false;
            }
            log(LocalizationService.T("Log_RebuildSuccess", unsignedApk));

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 5: Inject LemonLoader & Sign APK
            progress.Report((85, LocalizationService.T("Log_InjectingLemonLoader")));
            log(LocalizationService.T("Log_InjectingLemonLoader"));
            
            string lemonOutDir = Path.Combine(workDir, "lemon_out");
            string lemonTempDir = Path.Combine(workDir, "lemon_temp");
            string? signedApk = null;

            if (!string.IsNullOrEmpty(_lemonPatcherExe) && File.Exists(_lemonPatcherExe) &&
                !string.IsNullOrEmpty(_melonDataZip) && File.Exists(_melonDataZip))
            {
                var (lCode, lOut, lErr) = await RunProcessAsync(_lemonPatcherExe,
                    $"\"{unsignedApk}\" \"{lemonOutDir}\" \"{lemonTempDir}\" \"{_melonDataZip}\" \"{ModdedPackage}\"", 300000);

                if (lCode == 0)
                {
                    signedApk = Directory.GetFiles(lemonOutDir, "*.apk").FirstOrDefault();
                    if (!string.IsNullOrEmpty(signedApk))
                    {
                        log(LocalizationService.T("Log_LemonLoaderSuccess"));
                    }
                }
                else
                {
                    log($"LemonLoader patch note ({lCode}): {lOut} {lErr}");
                }
            }

            // Fallback to uber-apk-signer if lemon_patcher was not available
            if (string.IsNullOrEmpty(signedApk))
            {
                progress.Report((88, LocalizationService.T("Log_SigningApk")));
                log(LocalizationService.T("Log_SigningApk"));
                string signedDir = Path.Combine(workDir, "signed");
                Directory.CreateDirectory(signedDir);
                var (sCode, sOut, sErr) = await RunProcessAsync("java", $"-jar \"{_signerJar}\" -a \"{unsignedApk}\" -o \"{signedDir}\"", 300000);
                if (sCode != 0)
                {
                    log($"Signing error: {sOut} {sErr}");
                    return false;
                }
                signedApk = Directory.GetFiles(signedDir, "*.apk").FirstOrDefault();
            }

            if (string.IsNullOrEmpty(signedApk))
            {
                log("ERROR: Signed APK not found in output directory.");
                return false;
            }

            // LemonLoader's embedded signer can generate a different certificate on
            // each run. Re-sign every final APK with USM's persistent key so Android
            // accepts updates with `adb install -r` and retains app data.
            progress.Report((89, LocalizationService.T("Log_SigningApk")));
            string signingKeystore;
            try
            {
                signingKeystore = await EnsureStableQuestSigningKeystoreAsync(log);
            }
            catch (Exception ex)
            {
                log($"Could not prepare the persistent Quest signing key: {ex.Message}");
                return false;
            }

            string persistentSignedDir = Path.Combine(workDir, "persistent_signed");
            Directory.CreateDirectory(persistentSignedDir);
            var (stableSignCode, stableSignOut, stableSignErr) = await RunProcessAsync(
                "java",
                $"-jar \"{_signerJar}\" -a \"{signedApk}\" -o \"{persistentSignedDir}\" --allowResign --ksDebug \"{signingKeystore}\"",
                300000);
            if (stableSignCode != 0)
            {
                log($"Persistent APK signing failed: {stableSignOut} {stableSignErr}".Trim());
                return false;
            }

            string? persistentlySignedApk = Directory.GetFiles(persistentSignedDir, "*.apk").FirstOrDefault();
            if (string.IsNullOrEmpty(persistentlySignedApk))
            {
                log("Persistent APK signing finished without producing an APK.");
                return false;
            }
            signedApk = persistentlySignedApk;
            log("Final APK signed with USM's persistent Quest key.");
            log(LocalizationService.T("Log_SigningSuccess", signedApk));

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 6: Deploy to Quest if device connected
            if (!string.IsNullOrEmpty(serial))
            {
                var saveBackup = await BackupQuestSaveDataAsync(serial, log);

                progress.Report((92, LocalizationService.T("Log_InstallingApk", ModdedPackage)));
                var (instSuccess, instMsg) = await InstallApkAsync(serial, signedApk, log, saveBackup.SafeForCleanReinstall);
                if (!instSuccess)
                {
                    log($"Installation failed: {instMsg}");
                    return false;
                }

                bool saveRestored = await RestoreQuestSaveDataAsync(serial, saveBackup, log);
                if (!saveRestored && !string.IsNullOrEmpty(saveBackup.SavePath))
                {
                    log("The APK is installed but save restoration failed. The local backup was kept so it can be restored manually.");
                    return false;
                }

                progress.Report((95, LocalizationService.T("Log_SettingObb")));
                log(LocalizationService.T("Log_SettingObb"));
                string obbTargetDir = $"/sdcard/Android/obb/{ModdedPackage}";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {obbTargetDir}");
                string origObbDir = $"/sdcard/Android/obb/{OriginalPackage}";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell cp {origObbDir}/main.*.{OriginalPackage}.obb {obbTargetDir}/main.410.{ModdedPackage}.obb");

                progress.Report((97, LocalizationService.T("Log_GrantingPermissions")));
                await GrantStoragePermissionsAsync(serial, ModdedPackage, log);

                // Deploy the Quest mod DLL under the package's ModData directory.
                progress.Report((98, LocalizationService.T("Log_DeployingModDll")));
                await DeployModDllAsync(serial, ModdedPackage, log);

                if (skipIntroAndUnlockCampaign)
                {
                    progress.Report((99, LocalizationService.T("Log_InjectingSave")));
                    await SetupUnlockedSaveDataAsync(serial, saveBackup, log);
                }
            }
            else
            {
                log($"No device connected. The modded APK is ready at:\n{signedApk}");
            }

            progress.Report((100, LocalizationService.T("Log_PipelineComplete")));
            log(LocalizationService.T("Log_PipelineComplete"));
            return true;
        }

        public async Task<bool> DeployModDllAsync(string serial, string packageName, Action<string>? log = null)
        {
            try
            {
                string? modDll = _modDllPath;
                if (string.IsNullOrEmpty(modDll) || !File.Exists(modDll))
                {
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string? embeddedQuestMod = PackagedModAssemblyProvider.ExtractQuestModAssembly();
                    string[] candidates =
                    {
                        embeddedQuestMod ?? string.Empty,
                        Path.Combine(appDir, "UnpluggedCustomSongs.Quest.dll"),
                        Path.Combine(appDir, "UnpluggedCustomSongs.dll")
                    };
                    modDll = candidates.FirstOrDefault(candidate => File.Exists(candidate) && IsQuestModAssembly(candidate));
                }

                if (!string.IsNullOrEmpty(modDll) && File.Exists(modDll))
                {
                    log?.Invoke(LocalizationService.T("Log_DeployingModDll"));
                    if (!Regex.IsMatch(packageName, @"^[A-Za-z0-9_.]+$"))
                        throw new ArgumentException("Invalid Android package name for the mod folder.", nameof(packageName));

                    // Keep the mod assembly under the app's ModData tree, alongside
                    // the separate Songs directory managed by the library sync.
                    string targetDir = $"/sdcard/ModData/{packageName}/Mods";
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p \"{targetDir}\"");
                    var (pushCode, pushOut, pushErr) = await RunProcessAsync(
                        GetAdbPath(), $"-s {serial} push \"{modDll}\" \"{targetDir}/UnpluggedCustomSongs.Quest.dll\"", 30000);
                    if (pushCode != 0)
                    {
                        log?.Invoke($"Could not deploy the mod DLL to {targetDir}: {(pushOut + " " + pushErr).Trim()}");
                        return false;
                    }
                    // Remove older duplicate names and the stale DLL in the previous
                    // deployment directory so only the current Quest assembly remains.
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} shell rm -f \"{targetDir}/UnpluggedCustomSongs.dll\" \"/sdcard/MelonLoader/{packageName}/Mods/UnpluggedCustomSongs.Quest.dll\" \"/sdcard/MelonLoader/{packageName}/Mods/UnpluggedCustomSongs.dll\"");
                    log?.Invoke(LocalizationService.T("Log_ModDllSuccess"));
                    return true;
                }
                else
                {
                    log?.Invoke("Warning: UnpluggedCustomSongs.Quest.dll was not found in USM's package for deployment to Quest ModData.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Warning: Could not deploy mod DLL to Quest: {ex.Message}");
                return false;
            }
        }

        public async Task<HashSet<string>> GetQuestInstalledSongsAsync(string serial)
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {ModdedSongsPath}");
                var (code, stdout, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell ls -1 {ModdedSongsPath}/", 15000);
                if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        string name = line.Trim();
                        if (!string.IsNullOrEmpty(name) && !name.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                        {
                            installed.Add(name);
                        }
                    }
                }
            }
            catch { }
            return installed;
        }

        public async Task<(bool DirectoryExists, List<string> SongFiles, string Message)> ListQuestSongsAsync(string serial)
        {
            var (directoryCode, _, directoryError) = await RunProcessAsync(
                GetAdbPath(), $"-s {serial} shell test -d {ModdedSongsPath}", 15000);
            if (directoryCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(directoryError)
                    ? "Quest custom-songs folder was not found."
                    : directoryError.Trim();
                return (false, new List<string>(), message);
            }

            var (listCode, stdout, stderr) = await RunProcessAsync(
                GetAdbPath(), $"-s {serial} shell ls -1 {ModdedSongsPath}", 15000);
            if (listCode != 0)
            {
                return (true, new List<string>(), (stdout + " " + stderr).Trim());
            }

            var songs = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => name.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (true, songs, songs.Count == 0 ? "No .usm songs found on Quest." : $"Found {songs.Count} song(s) on Quest.");
        }

        public async Task<(bool Success, string Message)> DeleteQuestSongAsync(string serial, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains('/') || fileName.Contains('\\') ||
                !fileName.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Invalid Quest song filename.");
            }

            string remotePath = $"{ModdedSongsPath}/{fileName}";
            string shellQuotedPath = "'" + remotePath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            var (code, stdout, stderr) = await RunProcessAsync(
                GetAdbPath(), $"-s {serial} shell rm -f -- {shellQuotedPath}", 30000);
            if (code == 0) return (true, $"Deleted {fileName} from Quest.");
            return (false, (stdout + " " + stderr).Trim());
        }

        public async Task<int> SyncSongsToQuestAsync(
            string serial,
            string localSongsDir,
            Action<string> log,
            IProgress<(int Current, int Total)> progress,
            CancellationToken cancellationToken,
            IReadOnlyList<string>? specificSongPaths = null)
        {
            if (!Directory.Exists(localSongsDir))
            {
                log($"Songs directory does not exist: {localSongsDir}");
                return 0;
            }

            var songDirs = specificSongPaths != null && specificSongPaths.Count > 0 ? specificSongPaths.ToArray() : Directory.GetFiles(localSongsDir, "*.usm");

            if (songDirs.Length == 0)
            {
                log("No custom songs to sync.");
                return 0;
            }

            log(LocalizationService.T("Log_SyncStarting", songDirs.Length, ModdedSongsPath));
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {ModdedSongsPath}");

            int transferred = 0;
            for (int i = 0; i < songDirs.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string dir = songDirs[i];
                string songName = Path.GetFileName(dir);
                progress.Report((i + 1, songDirs.Length));
                log(LocalizationService.T("Log_SyncingSong", i + 1, songDirs.Length, songName));

                var (code, _, err) = await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{dir}\" {ModdedSongsPath}/", 120000);
                if (code == 0)
                {
                    transferred++;
                }
                else
                {
                    log($"Warning: Failed pushing {songName}: {err}");
                }
            }

            log(LocalizationService.T("Log_SyncComplete", transferred, songDirs.Length));
            return transferred;
        }

        public static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();

            var completedTask = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));
            if (completedTask == exitTask)
            {
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                return (process.ExitCode, stdout, stderr);
            }
            else
            {
                try { process.Kill(true); } catch { }
                return (-1, string.Empty, "Process timed out.");
            }
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessForWindowAsync(
            string fileName,
            string arguments,
            int captureWindowMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(captureWindowMs)) != exitTask)
            {
                try { process.Kill(true); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                return (-1, await stdoutTask, await stderrTask);
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}
