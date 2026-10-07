using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace USM.Core.Services.Quest
{
    public class QuestService
    {
        public const string OriginalPackage = "com.Anotherway.UnpluggedVR";
        public const string ModdedPackage = "com.Anotherway.UnpluggedVR.modded";
        public const string ModdedAppName = "Unplugged Modded";

        private string? _adbPath;
        private string? _apktoolJar;
        private string? _signerJar;

        public QuestService()
        {
            FindToolPaths();
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

            // 2. Locate Tools directory (apktool and uber-apk-signer)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchDirs =
            {
                Path.Combine(baseDir, "tools", "quest"),
                Path.Combine(baseDir, @"..\..\..\..\tools\quest"),
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "quest"),
                @"C:/REDACTED\.gemini\antigravity\scratch\UnpluggedSongStudio\tools\quest"
            };

            foreach (var dir in searchDirs)
            {
                string fullDir = Path.GetFullPath(dir);
                string apktool = Path.Combine(fullDir, "apktool.jar");
                string signer = Path.Combine(fullDir, "uber-apk-signer.jar");
                if (File.Exists(apktool) && File.Exists(signer))
                {
                    _apktoolJar = apktool;
                    _signerJar = signer;
                    break;
                }
            }
        }

        public string GetAdbPath() => _adbPath ?? "adb";
        public string? GetApktoolJar() => _apktoolJar;
        public string? GetSignerJar() => _signerJar;

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
            log?.Invoke($"Pulling APK from Quest: {remotePath} -> {localPath}");
            var (code, outStr, errStr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} pull \"{remotePath}\" \"{localPath}\"", 300000);
            if (code == 0 && File.Exists(localPath))
            {
                log?.Invoke($"APK pulled successfully ({new FileInfo(localPath).Length / (1024 * 1024)} MB).");
                return true;
            }
            log?.Invoke($"Failed to pull APK: {outStr} {errStr}");
            return false;
        }

        public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string localApkPath, Action<string>? log = null)
        {
            log?.Invoke($"Installing modded APK to Quest ({ModdedPackage})...");
            var (code, outStr, errStr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} install -r -d \"{localApkPath}\"", 300000);
            string full = (outStr + " " + errStr).Trim();
            bool success = full.Contains("Success", StringComparison.OrdinalIgnoreCase);
            if (success)
            {
                log?.Invoke("APK successfully installed on Quest!");
                return (true, "Installation successful!");
            }
            log?.Invoke($"Installation error: {full}");
            return (false, full);
        }

        public async Task GrantStoragePermissionsAsync(string serial, string packageName, Action<string>? log = null)
        {
            log?.Invoke("Granting storage and file access permissions on Quest...");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell appops set {packageName} MANAGE_EXTERNAL_STORAGE allow");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.READ_EXTERNAL_STORAGE");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.WRITE_EXTERNAL_STORAGE");

            // Setup songs directories
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/Unplugged/Songs");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/ModData/{packageName}/Songs");
            log?.Invoke("Quest storage permissions and directories ready.");
        }

        public async Task<bool> PatchAndDeployAsync(
            string? serial,
            string? customSourceApk,
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

            progress.Report((5, "Verifying Java, Apktool, and ADB..."));
            log("=== STARTING QUEST MODDING PIPELINE ===");

            if (string.IsNullOrEmpty(_apktoolJar) || !File.Exists(_apktoolJar))
            {
                log($"ERROR: apktool.jar not found! Please check tools/quest/apktool.jar");
                return false;
            }
            if (string.IsNullOrEmpty(_signerJar) || !File.Exists(_signerJar))
            {
                log($"ERROR: uber-apk-signer.jar not found! Please check tools/quest/uber-apk-signer.jar");
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
                log($"Querying package '{OriginalPackage}' on device {serial}...");

                string? remotePath = await GetPackageApkPathAsync(serial, OriginalPackage);
                if (string.IsNullOrEmpty(remotePath))
                {
                    log($"Original package '{OriginalPackage}' not found. Checking if modded version exists...");
                    remotePath = await GetPackageApkPathAsync(serial, ModdedPackage);
                }

                if (string.IsNullOrEmpty(remotePath))
                {
                    log("ERROR: Unplugged: Air Guitar was not found installed on this Quest.");
                    return false;
                }

                progress.Report((25, "Pulling APK from Quest..."));
                bool pulled = await PullApkAsync(serial, remotePath, sourceApk, log);
                if (!pulled) return false;
            }

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 2: Decompile APK
            progress.Report((40, "Decompiling APK with Apktool..."));
            log("Decompiling APK structure (this may take up to 1-2 minutes)...");
            string decompiledDir = Path.Combine(workDir, "decompiled");
            var (dCode, dOut, dErr) = await RunProcessAsync("java", $"-jar \"{_apktoolJar}\" d -f -o \"{decompiledDir}\" \"{sourceApk}\"", 300000);
            if (dCode != 0)
            {
                log($"Decompilation error: {dOut} {dErr}");
                return false;
            }
            log("APK decompiled successfully.");

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 3: Patch AndroidManifest and clone package
            progress.Report((60, "Patching Manifest and package identity..."));
            log("Configuring cloned package identity (com.anotherway.unplugged.modded) and mod permissions...");
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
            log("AndroidManifest.xml successfully patched.");

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
            log("Rebuilding APK (this may take 1-2 minutes)...");
            string unsignedApk = Path.Combine(workDir, "unplugged_modded_unsigned.apk");
            var (bCode, bOut, bErr) = await RunProcessAsync("java", $"-jar \"{_apktoolJar}\" b \"{decompiledDir}\" -o \"{unsignedApk}\"", 300000);
            if (bCode != 0)
            {
                log($"Rebuild error: {bOut} {bErr}");
                return false;
            }
            log($"APK rebuilt: {unsignedApk}");

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 5: Sign APK
            progress.Report((85, "Signing APK with Uber-APK-Signer..."));
            log("Signing APK (v2/v3 signatures)...");
            string signedDir = Path.Combine(workDir, "signed");
            Directory.CreateDirectory(signedDir);
            var (sCode, sOut, sErr) = await RunProcessAsync("java", $"-jar \"{_signerJar}\" -a \"{unsignedApk}\" -o \"{signedDir}\"", 300000);
            if (sCode != 0)
            {
                log($"Signing error: {sOut} {sErr}");
                return false;
            }

            string? signedApk = Directory.GetFiles(signedDir, "*.apk").FirstOrDefault();
            if (string.IsNullOrEmpty(signedApk))
            {
                log("ERROR: Signed APK not found in output directory.");
                return false;
            }
            log($"Signed APK ready: {signedApk}");

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 6: Deploy to Quest if device connected
            if (!string.IsNullOrEmpty(serial))
            {
                progress.Report((92, "Installing modded APK to Quest..."));
                var (instSuccess, instMsg) = await InstallApkAsync(serial, signedApk, log);
                if (!instSuccess)
                {
                    log($"Installation failed: {instMsg}");
                    return false;
                }

                progress.Report((96, "Ensuring OBB expansion file exists..."));
                log("Setting up game data OBB expansion file on Quest...");
                string obbTargetDir = $"/sdcard/Android/obb/{ModdedPackage}";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {obbTargetDir}");
                string origObbDir = $"/sdcard/Android/obb/{OriginalPackage}";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell cp {origObbDir}/main.*.{OriginalPackage}.obb {obbTargetDir}/main.410.{ModdedPackage}.obb");

                progress.Report((98, "Configuring storage permissions..."));
                await GrantStoragePermissionsAsync(serial, ModdedPackage, log);
            }
            else
            {
                log($"No device connected. The modded APK is ready at:\n{signedApk}");
            }

            progress.Report((100, "Completed successfully!"));
            log("=== QUEST MODDING PIPELINE FINISHED SUCCESSFULLY ===");
            return true;
        }

        public async Task<int> SyncSongsToQuestAsync(
            string serial,
            string localSongsDir,
            Action<string> log,
            IProgress<(int Current, int Total)> progress,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(localSongsDir))
            {
                log($"Songs directory does not exist: {localSongsDir}");
                return 0;
            }

            var songDirs = Directory.GetDirectories(localSongsDir);
            if (songDirs.Length == 0)
            {
                log("No custom songs found in local directory to sync.");
                return 0;
            }

            log($"Syncing {songDirs.Length} custom songs to Quest (/sdcard/Unplugged/Songs)...");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/Unplugged/Songs");

            int transferred = 0;
            for (int i = 0; i < songDirs.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string dir = songDirs[i];
                string songName = Path.GetFileName(dir);
                progress.Report((i + 1, songDirs.Length));
                log($"[{i + 1}/{songDirs.Length}] Pushing {songName}...");

                var (code, _, err) = await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{dir}\" /sdcard/Unplugged/Songs/", 120000);
                if (code == 0)
                {
                    transferred++;
                }
                else
                {
                    log($"Warning: Failed pushing {songName}: {err}");
                }
            }

            log($"Sync finished! {transferred}/{songDirs.Length} songs transferred to Quest.");
            return transferred;
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments, int timeoutMs = 60000)
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
    }
}
