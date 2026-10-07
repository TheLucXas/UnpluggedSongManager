using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services.Localization;

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
        private string? _lemonPatcherExe;
        private string? _melonDataZip;
        private string? _modDllPath;

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

            // 2. Locate Tools directory (apktool, uber-apk-signer, lemon_patcher, melon_data)
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
                string lemonPatcher = Path.Combine(fullDir, "lemon_patcher", "lemon_patcher.exe");
                string melonData = Path.Combine(fullDir, "melon_data.zip");
                string modDll = Path.Combine(fullDir, "UnpluggedCustomSongs.dll");

                if (File.Exists(apktool) && File.Exists(signer))
                {
                    _apktoolJar = apktool;
                    _signerJar = signer;
                }
                if (File.Exists(lemonPatcher)) _lemonPatcherExe = lemonPatcher;
                if (File.Exists(melonData)) _melonDataZip = melonData;
                if (File.Exists(modDll)) _modDllPath = modDll;
            }

            if (_modDllPath == null)
            {
                string[] modCandidates =
                {
                    @"C:/REDACTED\.gemini\antigravity\scratch\UnpluggedSongStudio\tools\quest\UnpluggedCustomSongs.dll",
                    @"C:/REDACTED\.gemini\antigravity\scratch\UnpluggedSongStudio\mod\UnpluggedCustomSongs\UnpluggedCustomSongs.dll",
                    @"C:\SteamLibrary\steamapps\common\Unplugged\Mods\UnpluggedCustomSongs.dll"
                };
                foreach (var c in modCandidates)
                {
                    if (File.Exists(c)) { _modDllPath = c; break; }
                }
            }
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

        public async Task<(bool Success, string Message)> InstallApkAsync(string serial, string localApkPath, Action<string>? log = null)
        {
            log?.Invoke(LocalizationService.T("Log_InstallingApk", ModdedPackage));

            // Clean any stale temp APKs from /data/local/tmp on Quest to maximize free space
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell \"rm -f /data/local/tmp/*.apk\"");

            // Try install with --no-incremental to bypass adb incremental service storage restriction
            var (code, outStr, errStr) = await RunProcessAsync(GetAdbPath(), $"-s {serial} install --no-incremental -r -d \"{localApkPath}\"", 300000);
            string full = (outStr + " " + errStr).Trim();
            bool success = full.Contains("Success", StringComparison.OrdinalIgnoreCase);

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
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell appops set {packageName} MANAGE_EXTERNAL_STORAGE allow");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.READ_EXTERNAL_STORAGE");
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell pm grant {packageName} android.permission.WRITE_EXTERNAL_STORAGE");

            // Setup songs directories
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/Unplugged/Songs");
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
                return (true, "Uninstalled successfully.");
            }
            log?.Invoke($"Failed to uninstall: {output}");
            return (false, output);
        }

        public static string GenerateUnlockedSavePayload()
        {
            // Encrypted save data that marks tutorial completed, intro skipped, and unlocks base campaign albums 1-5
            string plainJson = "{\"SavedData\":{" +
                "\"HasDoneTutorial\":{\"Type\":\"System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"true\"}," +
                "\"ShowIntro\":{\"Type\":\"System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"false\"}," +
                "\"isReleaseVersion\":{\"Type\":\"System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"true\"}," +
                "\"AlbumCompleted\":{\"Type\":\"System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"[\\\"CampaignAlbum1\\\",\\\"CampaignAlbum2\\\",\\\"CampaignAlbum3\\\",\\\"CampaignAlbum4\\\",\\\"CampaignAlbum5\\\"]\"}," +
                "\"Fans\":{\"Type\":\"System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"5000000\"}," +
                "\"PlayedSongs\":{\"Type\":\"System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"10\"}," +
                "\"PurchasedGuitars\":{\"Type\":\"System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"[\\\"SchecterGuitar1\\\",\\\"SchecterGuitar2\\\",\\\"SchecterGuitar3\\\"]\"}," +
                "\"PurchasedPicks\":{\"Type\":\"System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"[\\\"DefaultPick1\\\",\\\"DefaultPick2\\\"]\"}," +
                "\"SelectedAlbumID\":{\"Type\":\"System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\",\"JsonValue\":\"\\\"CampaignAlbum1\\\"\"}" +
                "}}";

            return EncryptSaveData("b14ca5898a4e4133bbce2ea2315a1916", plainJson);
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

        public async Task SetupUnlockedSaveDataAsync(string serial, string packageName, Action<string>? log = null)
        {
            try
            {
                log?.Invoke(LocalizationService.T("Log_InjectingSave"));
                string encryptedData = GenerateUnlockedSavePayload();
                string tempFile = Path.Combine(Path.GetTempPath(), "SavedData.json");
                await File.WriteAllTextAsync(tempFile, encryptedData);

                // Push to Android/data persistentDataPath
                string targetPath1 = $"/sdcard/Android/data/{packageName}/files";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {targetPath1}");
                await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{tempFile}\" \"{targetPath1}/SavedData.json\"");

                // Also push to ModData as fallback
                string targetPath2 = $"/sdcard/ModData/{packageName}/files";
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {targetPath2}");
                await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{tempFile}\" \"{targetPath2}/SavedData.json\"");

                try { File.Delete(tempFile); } catch { }
                log?.Invoke(LocalizationService.T("Log_SaveSuccess"));
            }
            catch (Exception ex)
            {
                log?.Invoke($"Warning: Could not inject unlocked save data: {ex.Message}");
            }
        }

        public async Task<bool> PatchAndDeployAsync(
            string? serial,
            string? customSourceApk,
            bool skipTutorialAndUnlock,
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

            progress.Report((5, LocalizationService.T("Log_VerifyTools")));
            log(LocalizationService.T("Log_StartingPipeline"));

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
            log(LocalizationService.T("Log_PatchingManifest"));
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
            log(LocalizationService.T("Log_SigningSuccess", signedApk));

            if (cancellationToken.IsCancellationRequested) return false;

            // Step 6: Deploy to Quest if device connected
            if (!string.IsNullOrEmpty(serial))
            {
                progress.Report((92, LocalizationService.T("Log_InstallingApk", ModdedPackage)));
                var (instSuccess, instMsg) = await InstallApkAsync(serial, signedApk, log);
                if (!instSuccess)
                {
                    log($"Installation failed: {instMsg}");
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

                // Deploy UnpluggedCustomSongs mod DLL to Quest Mods folder
                progress.Report((98, LocalizationService.T("Log_DeployingModDll")));
                await DeployModDllAsync(serial, ModdedPackage, log);

                if (skipTutorialAndUnlock)
                {
                    progress.Report((99, LocalizationService.T("Log_InjectingSave")));
                    await SetupUnlockedSaveDataAsync(serial, ModdedPackage, log);
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
                    string candidate = @"C:/REDACTED\.gemini\antigravity\scratch\UnpluggedSongStudio\tools\quest\UnpluggedCustomSongs.dll";
                    if (File.Exists(candidate)) modDll = candidate;
                }

                if (!string.IsNullOrEmpty(modDll) && File.Exists(modDll))
                {
                    log?.Invoke(LocalizationService.T("Log_DeployingModDll"));
                    string targetModDir1 = $"/sdcard/ModData/{packageName}/Mods";
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {targetModDir1}");
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{modDll}\" \"{targetModDir1}/UnpluggedCustomSongs.dll\"");

                    string targetModDir2 = $"/sdcard/ModData/{OriginalPackage}/Mods";
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p {targetModDir2}");
                    await RunProcessAsync(GetAdbPath(), $"-s {serial} push \"{modDll}\" \"{targetModDir2}/UnpluggedCustomSongs.dll\"");
                    log?.Invoke(LocalizationService.T("Log_ModDllSuccess"));
                    return true;
                }
                else
                {
                    log?.Invoke("Warning: UnpluggedCustomSongs.dll not found to deploy to Quest Mods folder.");
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
                await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/Unplugged/Songs");
                var (code, stdout, _) = await RunProcessAsync(GetAdbPath(), $"-s {serial} shell ls -1 /sdcard/Unplugged/Songs/", 15000);
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

        public async Task<int> SyncSongsToQuestAsync(
            string serial,
            string localSongsDir,
            Action<string> log,
            IProgress<(int Current, int Total)> progress,
            CancellationToken cancellationToken,
            IReadOnlyList<string>? specificSongDirs = null)
        {
            if (!Directory.Exists(localSongsDir))
            {
                log($"Songs directory does not exist: {localSongsDir}");
                return 0;
            }

            var songDirs = specificSongDirs != null && specificSongDirs.Count > 0
                ? specificSongDirs.ToArray()
                : Directory.GetDirectories(localSongsDir);

            if (songDirs.Length == 0)
            {
                log("No custom songs to sync.");
                return 0;
            }

            log(LocalizationService.T("Log_SyncStarting", songDirs.Length));
            await RunProcessAsync(GetAdbPath(), $"-s {serial} shell mkdir -p /sdcard/Unplugged/Songs");

            int transferred = 0;
            for (int i = 0; i < songDirs.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string dir = songDirs[i];
                string songName = Path.GetFileName(dir);
                progress.Report((i + 1, songDirs.Length));
                log(LocalizationService.T("Log_SyncingSong", i + 1, songDirs.Length, songName));

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

            log(LocalizationService.T("Log_SyncComplete", transferred, songDirs.Length));
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
