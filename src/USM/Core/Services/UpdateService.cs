using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services.Localization;

namespace USM.Core.Services
{
    public class GitHubAssetInfo
    {
        public string Digest { get; set; } = string.Empty;
        public string? ModVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public string BrowserDownloadUrl { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public class GitHubReleaseInfo
    {
        public string TagName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
        public List<GitHubAssetInfo> Assets { get; set; } = new();
    }

    public class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "1.0.0";
        public string LatestVersion { get; set; } = string.Empty;
        public GitHubReleaseInfo? Release { get; set; }
        public GitHubAssetInfo? ModAsset { get; set; }
        public GitHubAssetInfo? QuestModAsset { get; set; }
        public GitHubAssetInfo? UsmAsset { get; set; }
        public GitHubAssetInfo? QuestToolsAsset { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class UpdateService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public const string DefaultOwner = "TheLucXas";
        public const string DefaultRepo = "UnpluggedSongManager";

        public string CurrentAppVersion
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(
            string repoOwner = DefaultOwner,
            string repoName = DefaultRepo,
            string? token = null,
            CancellationToken cancellationToken = default)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = CurrentAppVersion
            };

            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "USM-SongManager/2.0");
                request.Headers.Add("Accept", "application/vnd.github.v3+json");

                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Add("Authorization", $"Bearer {token}");
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    result.Success = true;
                    result.IsUpdateAvailable = false;
                    result.Message = LocalizationService.T("Settings_NoReleasesFound");
                    return result;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    result.Success = false;
                    result.Message = LocalizationService.T("Settings_RateLimitExceeded");
                    return result;
                }

                if (!response.IsSuccessStatusCode)
                {
                    result.Success = false;
                    result.Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                var release = ParseReleaseJson(json);
                if (release == null)
                {
                    result.Success = false;
                    result.Message = "Failed to parse release information.";
                    return result;
                }

                result.Success = true;
                result.Release = release;
                result.LatestVersion = CleanVersionString(release.TagName);
                result.ModAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.Equals("UnpluggedCustomSongs.PC.dll", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a =>
                        a.Name.Equals("UnpluggedCustomSongs.dll", StringComparison.OrdinalIgnoreCase));

                result.UsmAsset = release.Assets.FirstOrDefault(a =>
                    (a.Name.Contains("USM", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Unplugged", StringComparison.OrdinalIgnoreCase)) &&
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        a.Name.Contains("USM", StringComparison.OrdinalIgnoreCase));

                result.QuestToolsAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.Contains("quest-tools", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Contains("quest_tools", StringComparison.OrdinalIgnoreCase));
                result.QuestModAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.Equals("UnpluggedCustomSongs.Quest.dll", StringComparison.OrdinalIgnoreCase));
                await Task.WhenAll(ReadReleaseModVersionAsync(result.ModAsset, cancellationToken),
                    ReadReleaseModVersionAsync(result.QuestModAsset, cancellationToken));

                result.IsUpdateAvailable = result.UsmAsset != null &&
                    CompareVersions(result.LatestVersion, result.CurrentVersion) > 0;

                if (result.IsUpdateAvailable)
                {
                    result.Message = LocalizationService.T("Settings_UpdateAvailable", release.TagName);
                }
                else
                {
                    result.Message = LocalizationService.T("Settings_UpToDate");
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = LocalizationService.T("Settings_UpdateCheckFailed", ex.Message);
                return result;
            }
        }

        private static async Task ReadReleaseModVersionAsync(GitHubAssetInfo? asset, CancellationToken cancellationToken)
        {
            if (asset == null || asset.Size > 16 * 1024 * 1024 || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.Host != "github.com") return;
            string path = Path.Combine(Path.GetTempPath(), $"USM_ReleaseMod_{Guid.NewGuid():N}.dll");
            try
            {
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) return;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = File.Create(path))
                {
                    byte[] buffer = new byte[81920];
                    int count;
                    long total = 0;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += count;
                        if (total > 16 * 1024 * 1024) return;
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                }
                asset.ModVersion = ModAssemblyMetadata.GetVersion(path);
            }
            catch { /* A failed mod lookup must not suppress a valid USM release. */ }
            finally { try { File.Delete(path); } catch { } }
        }

        public async Task<(bool Success, string Message)> DownloadAndInstallModAssetAsync(
            string downloadUrl,
            string targetPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default,
            string expectedAssemblyName = "UnpluggedCustomSongs")
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.Add("User-Agent", "USM-SongManager/2.0");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Download failed: HTTP {(int)response.StatusCode}");
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                string tempFile = Path.Combine(Path.GetTempPath(), $"UnpluggedMod_{Guid.NewGuid():N}.dll");

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            progress.Report((double)totalRead / totalBytes * 100);
                        }
                    }
                }

                string? assemblyName = AssemblyName.GetAssemblyName(tempFile).Name;
                if (!string.Equals(assemblyName, expectedAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(tempFile); } catch { }
                    return (false, "The downloaded DLL does not match the requested mod platform, so it was not installed.");
                }

                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(tempFile, targetPath, true);
                try { File.Delete(tempFile); } catch { }

                return (true, LocalizationService.T("Settings_ModUpdatedSuccess"));
            }
            catch (Exception ex)
            {
                return (false, LocalizationService.T("Settings_ModUpdateFailed", ex.Message));
            }
        }

        public async Task<(bool Success, string Message, string? BatchScriptToExecute)> DownloadAndPrepareAppUpdateAsync(
            string downloadUrl,
            string fileName,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string tempFile = string.Empty;
            string stagingDir = string.Empty;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.Add("User-Agent", "USM-SongManager/2.0");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Download failed: HTTP {(int)response.StatusCode}", null);
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                string ext = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) ext = ".zip";

                tempFile = Path.Combine(Path.GetTempPath(), $"USM_Update_{Guid.NewGuid():N}{ext}");

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            progress.Report((double)totalRead / totalBytes * 100);
                        }
                    }
                }

                stagingDir = Path.Combine(Path.GetTempPath(), $"USM_Extracted_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                string exeName = "USM.exe";
                try
                {
                    string? runningName = Process.GetCurrentProcess().MainModule?.ModuleName;
                    if (!string.IsNullOrEmpty(runningName)) exeName = runningName;
                }
                catch { }

                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // The portable executable is the update payload, not an installer.
                    // Stage it for the same replace-and-restart flow as a ZIP release.
                    File.Copy(tempFile, Path.Combine(stagingDir, exeName));
                }
                else if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempFile, stagingDir, overwriteFiles: true);
                }
                else
                {
                    throw new InvalidDataException($"Unsupported USM update file: {fileName}");
                }

                string sourceDir = stagingDir;
                var subDirs = Directory.GetDirectories(stagingDir);
                var rootFiles = Directory.GetFiles(stagingDir);
                if (rootFiles.Length == 0 && subDirs.Length == 1)
                {
                    sourceDir = subDirs[0];
                }

                string targetAppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!File.Exists(Path.Combine(sourceDir, exeName)))
                    throw new InvalidDataException($"USM update does not contain {exeName}.");

                string batchFile = Path.Combine(Path.GetTempPath(), $"usm_updater_{Guid.NewGuid():N}.bat");
                int pid = Environment.ProcessId;

                string batchContent = 
                    "@echo off\r\n" +
                    "chcp 65001 > nul\r\n" +
                    "title USM Updater\r\n" +
                    "echo ====================================================\r\n" +
                    "echo              USM - Updating Application\r\n" +
                    "echo ====================================================\r\n" +
                    "echo.\r\n" +
                    "echo Waiting for USM to exit...\r\n" +
                    ":wait_loop\r\n" +
                    "tasklist /fi \"PID eq " + pid + "\" 2>nul | find /i \"" + pid + "\" >nul\r\n" +
                    "if \"%ERRORLEVEL%\"==\"0\" (\r\n" +
                    "    timeout /t 1 /nobreak >nul\r\n" +
                    "    goto wait_loop\r\n" +
                    ")\r\n" +
                    "echo Copying updated files...\r\n" +
                    "timeout /t 1 /nobreak >nul\r\n" +
                    "xcopy \"" + sourceDir + "\\*\" \"" + targetAppDir + "\\\" /E /Y /I /Q >nul\r\n" +
                    "if errorlevel 1 (\r\n" +
                    "    robocopy \"" + sourceDir + "\" \"" + targetAppDir + "\" /E /IS /IT /NP /R:3 /W:1 >nul\r\n" +
                    ")\r\n" +
                    "echo Restarting USM...\r\n" +
                    "timeout /t 1 /nobreak >nul\r\n" +
                    "start \"\" \"" + Path.Combine(targetAppDir, exeName) + "\"\r\n" +
                    "rd /s /q \"" + stagingDir + "\" >nul 2>&1\r\n" +
                    "del \"" + tempFile + "\" >nul 2>&1\r\n" +
                    "(goto) 2>nul & del \"%~f0\"\r\n";

                File.WriteAllText(batchFile, batchContent);
                return (true, "Update prepared successfully.", batchFile);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
                if (!string.IsNullOrEmpty(stagingDir) && Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
                return (false, LocalizationService.T("Settings_UpdateAppFailed", ex.Message), null);
            }
        }

        public static void OpenUrlInBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static GitHubReleaseInfo? ParseReleaseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var release = new GitHubReleaseInfo
                {
                    TagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    HtmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "",
                    Body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : ""
                };

                if (root.TryGetProperty("published_at", out var pub) && pub.TryGetDateTime(out var dt))
                {
                    release.PublishedAt = dt;
                }

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var asset = new GitHubAssetInfo
                        {
                            Name = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "",
                            BrowserDownloadUrl = a.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "",
                            Digest = a.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "",
                            Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0
                        };
                        release.Assets.Add(asset);
                    }
                }

                return release;
            }
            catch
            {
                return null;
            }
        }

        public static string CleanVersionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
            raw = raw.Trim();
            if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(1).Trim();
            }
            return raw;
        }

        public static int CompareVersions(string verA, string verB)
        {
            string cleanA = CleanVersionString(verA);
            string cleanB = CleanVersionString(verB);

            if (Version.TryParse(cleanA, out var parsedA) && Version.TryParse(cleanB, out var parsedB))
            {
                // A mod can report "1.0" while its project reports "1.0.0".
                // Treat omitted trailing components as zero for update notices.
                var normalizedA = new Version(parsedA.Major, parsedA.Minor,
                    Math.Max(parsedA.Build, 0), Math.Max(parsedA.Revision, 0));
                var normalizedB = new Version(parsedB.Major, parsedB.Minor,
                    Math.Max(parsedB.Build, 0), Math.Max(parsedB.Revision, 0));
                return normalizedA.CompareTo(normalizedB);
            }

            // Fallback string compare
            return string.Compare(cleanA, cleanB, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<(bool Success, string Message)> DownloadAndInstallMelonLoaderAsync(
            string gamePath,
            IProgress<(double Progress, string Status)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            {
                return (false, "Game directory not found.");
            }

            string downloadUrl = "https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip";
            string tempZip = Path.Combine(Path.GetTempPath(), $"MelonLoader_x64_{Guid.NewGuid():N}.zip");

            try
            {
                progress?.Report((5, LocalizationService.T("Settings_MelonDownloading", 5)));

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.Add("User-Agent", "USM-SongManager/2.0");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Download failed: HTTP {(int)response.StatusCode}");
                }

                long totalBytes = response.Content.Headers.ContentLength ?? 18200000L;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            double pct = Math.Min(70.0, ((double)totalRead / totalBytes) * 70.0);
                            progress.Report((pct, LocalizationService.T("Settings_MelonDownloading", pct)));
                        }
                    }
                }

                progress?.Report((75, LocalizationService.T("Settings_MelonExtracting")));

                // Extract zip into game directory
                ZipFile.ExtractToDirectory(tempZip, gamePath, overwriteFiles: true);

                // Ensure Mods folder exists
                string modsDir = Path.Combine(gamePath, "Mods");
                if (!Directory.Exists(modsDir))
                {
                    Directory.CreateDirectory(modsDir);
                }

                // Deploy UnpluggedCustomSongs mod DLL if available
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] modCandidates =
                {
                    Path.Combine(appDir, "UnpluggedCustomSongs.PC.dll"),
                    Path.Combine(appDir, "UnpluggedCustomSongs.dll"),
                    Path.Combine(appDir, @"..\..\..\..\mod\UnpluggedCustomSongs\bin\Release\netstandard2.0\UnpluggedCustomSongs.dll"),
                    Path.Combine(appDir, @"..\..\..\..\mod\UnpluggedCustomSongs\UnpluggedCustomSongs.dll"),
                    Path.Combine(Directory.GetCurrentDirectory(), "UnpluggedCustomSongs.PC.dll"),
                    Path.Combine(Directory.GetCurrentDirectory(), @"mod\UnpluggedCustomSongs\bin\Release\netstandard2.0\UnpluggedCustomSongs.dll"),
                    Path.Combine(Directory.GetCurrentDirectory(), "UnpluggedCustomSongs.dll")
                };

                string? pcMod = GameDetector.FindPcModAssembly(modCandidates);
                if (pcMod != null)
                {
                    try
                    {
                        File.Copy(pcMod, Path.Combine(modsDir, "UnpluggedCustomSongs.dll"), overwrite: true);
                    }
                    catch { }
                }

                progress?.Report((100, LocalizationService.T("Settings_MelonSuccess")));
                return (true, LocalizationService.T("Settings_MelonSuccess"));
            }
            catch (Exception ex)
            {
                return (false, LocalizationService.T("Settings_MelonFailed", ex.Message));
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        public (bool Success, string Message) UninstallMelonLoader(string gamePath)
        {
            try
            {
                if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
                    return (false, "Game directory not found.");

                string versionDll = Path.Combine(gamePath, "version.dll");
                if (File.Exists(versionDll)) File.Delete(versionDll);

                string notice = Path.Combine(gamePath, "NOTICE.txt");
                if (File.Exists(notice)) File.Delete(notice);

                string melonDir = Path.Combine(gamePath, "MelonLoader");
                if (Directory.Exists(melonDir)) Directory.Delete(melonDir, recursive: true);

                return (true, LocalizationService.T("Settings_MelonUninstalled"));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string Message)> DownloadAndCacheQuestToolsAsync(
            string cacheDir,
            string? overrideUrl = null,
            IProgress<(double Progress, string Status)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);

                string downloadUrl = overrideUrl ?? string.Empty;
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    try
                    {
                        var updateRes = await CheckForUpdatesAsync(cancellationToken: cancellationToken);
                        if (updateRes.QuestToolsAsset != null && !string.IsNullOrEmpty(updateRes.QuestToolsAsset.BrowserDownloadUrl))
                        {
                            downloadUrl = updateRes.QuestToolsAsset.BrowserDownloadUrl;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = "https://github.com/TheLucXas/UnpluggedSongManager/releases/download/v2.0.0/quest-tools.zip";
                }

                progress?.Report((5, LocalizationService.T("Log_QuestToolsDownloading", 5)));

                string tempZip = Path.Combine(Path.GetTempPath(), $"QuestTools_{Guid.NewGuid():N}.zip");
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                    request.Headers.Add("User-Agent", "USM-SongManager/2.0");

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (false, $"Download failed: HTTP {(int)response.StatusCode}");
                    }

                    long totalBytes = response.Content.Headers.ContentLength ?? 55000000L;
                    await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                            totalRead += bytesRead;
                            if (totalBytes > 0 && progress != null)
                            {
                                double pct = Math.Min(80.0, ((double)totalRead / totalBytes) * 80.0);
                                progress.Report((pct, LocalizationService.T("Log_QuestToolsDownloading", pct)));
                            }
                        }
                    }

                    progress?.Report((85, LocalizationService.T("Log_QuestToolsExtracting")));
                    ZipFile.ExtractToDirectory(tempZip, cacheDir, overwriteFiles: true);

                    progress?.Report((100, LocalizationService.T("Log_QuestToolsCached", cacheDir)));
                    return (true, LocalizationService.T("Log_QuestToolsCached", cacheDir));
                }
                finally
                {
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                }
            }
            catch (Exception ex)
            {
                return (false, LocalizationService.T("Log_QuestToolsFailed", ex.Message));
            }
        }
    }
}
