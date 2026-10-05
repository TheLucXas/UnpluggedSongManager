using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Models;

namespace USM.Core.Services
{
    public class ChorusClient
    {
        private const string SearchUrl = "https://api.enchor.us/search";
        private const string FilesUrl = "https://files.enchor.us";
        private const string UserAgent = "USM/1.0 (VR Rhythm Game Mod Tool)";

        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        static ChorusClient()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public static async Task<(int Total, List<ChorusChart> Charts)> SearchAsync(
            string query,
            int page = 1,
            int perPage = 25,
            string instrument = "guitar",
            CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["search"] = string.IsNullOrWhiteSpace(query) ? "*" : query.Trim(),
                ["page"] = page,
                ["per_page"] = perPage,
                ["instrument"] = string.IsNullOrEmpty(instrument) ? null : instrument,
                ["source"] = "api"
            };

            string jsonString = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            try
            {
                using var response = await HttpClient.PostAsync(SearchUrl, content, ct);
                response.EnsureSuccessStatusCode();

                string responseText = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                int total = root.TryGetProperty("total", out var totalProp) ? totalProp.GetInt32() : 0;
                var charts = new List<ChorusChart>();

                if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        string md5 = item.TryGetProperty("md5", out var md5P) ? md5P.GetString() ?? "" : "";
                        string artMd5 = item.TryGetProperty("albumArtMd5", out var artP) ? artP.GetString() ?? "" : "";

                        int diffGuitar = -1;
                        if (item.TryGetProperty("diff_guitar", out var dgP) && dgP.ValueKind == JsonValueKind.Number)
                            diffGuitar = dgP.GetInt32();

                        int diffBass = -1;
                        if (item.TryGetProperty("diff_bass", out var dbP) && dbP.ValueKind == JsonValueKind.Number)
                            diffBass = dbP.GetInt32();

                        double previewSec = 30.0;
                        if (item.TryGetProperty("preview_start_time", out var pstP))
                        {
                            if (pstP.ValueKind == JsonValueKind.Number) previewSec = pstP.GetDouble() / 1000.0;
                            else if (pstP.ValueKind == JsonValueKind.String && double.TryParse(pstP.GetString(), out double d)) previewSec = d / 1000.0;
                        }

                        int songLen = 0;
                        if (item.TryGetProperty("song_length", out var slP) && slP.ValueKind == JsonValueKind.Number)
                            songLen = slP.GetInt32();

                        string durationStr = "--:--";
                        if (songLen > 0)
                        {
                            int m = songLen / 60;
                            int s = songLen % 60;
                            durationStr = $"{m}:{s:D2}";
                        }

                        var chart = new ChorusChart
                        {
                            Id = item.TryGetProperty("chartId", out var idP) ? idP.ToString() : md5,
                            Md5 = md5,
                            Title = item.TryGetProperty("name", out var nameP) ? nameP.GetString() ?? "Unknown Title" : "Unknown Title",
                            Artist = item.TryGetProperty("artist", out var artNameP) ? artNameP.GetString() ?? "Unknown Artist" : "Unknown Artist",
                            Album = item.TryGetProperty("album", out var albP) ? albP.GetString() ?? "" : "",
                            Year = item.TryGetProperty("year", out var yrP) ? yrP.ToString() : "",
                            Charter = item.TryGetProperty("charter", out var chP) ? chP.GetString() ?? "Unknown" : "Unknown",
                            Duration = durationStr,
                            DurationSeconds = songLen,
                            DiffGuitar = diffGuitar,
                            DiffBass = diffBass,
                            PreviewStart = previewSec,
                            DownloadUrl = !string.IsNullOrEmpty(md5) ? $"{FilesUrl}/{md5}.sng" : null,
                            ArtUrl = !string.IsNullOrEmpty(artMd5) ? $"{FilesUrl}/{artMd5}.jpg" : null
                        };

                        charts.Add(chart);
                    }
                }

                return (total, charts);
            }
            catch (TaskCanceledException)
            {
                throw new TimeoutException("Connection to Chorus timed out. Check your firewall or antivirus settings.");
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Network error connecting to Chorus: {ex.Message}", ex);
            }
        }

        public static async Task DownloadFileAsync(
            string url,
            string targetPath,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);

            byte[] buffer = new byte[64 * 1024];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes);
                }
            }
        }
    }
}
