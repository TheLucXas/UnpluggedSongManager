using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        public string CurrentVersion { get; set; } = "2.0.0";
        public string LatestVersion { get; set; } = string.Empty;
        public GitHubReleaseInfo? Release { get; set; }
        public GitHubAssetInfo? ModAsset { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class UpdateService
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public const string DefaultOwner = "TheLucXas";
        public const string DefaultRepo = "USM";

        public string CurrentAppVersion
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "2.0.0";
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
                    a.Name.Equals("UnpluggedCustomSongs.dll", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

                result.IsUpdateAvailable = CompareVersions(result.LatestVersion, result.CurrentVersion) > 0;

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

        public async Task<(bool Success, string Message)> DownloadAndInstallModAssetAsync(
            string downloadUrl,
            string targetPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
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
                return parsedA.CompareTo(parsedB);
            }

            // Fallback string compare
            return string.Compare(cleanA, cleanB, StringComparison.OrdinalIgnoreCase);
        }
    }
}
