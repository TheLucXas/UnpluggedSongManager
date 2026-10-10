using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            int perPage = 30,
            string instrument = "guitar",
            string? sortType = null,
            string? sortDirection = null,
            CancellationToken ct = default)
        {
            var payload = new
            {
                search = string.IsNullOrWhiteSpace(query) ? "*" : query.Trim(),
                page = page,
                per_page = perPage,
                instrument = string.IsNullOrEmpty(instrument) ? null : instrument,
                source = "api",
                sort = string.IsNullOrEmpty(sortType) ? null : new
                {
                    type = sortType,
                    direction = sortDirection ?? "desc"
                }
            };

            string jsonString = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            using var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            try
            {
                using var response = await HttpClient.PostAsync(SearchUrl, content, ct);
                response.EnsureSuccessStatusCode();

                string responseText = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                int total = root.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out int totalValue)
                    ? totalValue
                    : root.TryGetProperty("found", out var foundProp) && foundProp.TryGetInt32(out int foundValue)
                        ? foundValue
                        : 0;
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

                        // Enchor API returns song_length in milliseconds
                        int songLenSec = songLen > 1000 ? songLen / 1000 : songLen;

                        string durationStr = "--:--";
                        if (songLenSec > 0)
                        {
                            int m = songLenSec / 60;
                            int s = songLenSec % 60;
                            durationStr = $"{m}:{s:D2}";
                        }

                        string title = item.TryGetProperty("name", out var nameP) ? nameP.GetString() ?? "Unknown Title" : "Unknown Title";
                        string artist = item.TryGetProperty("artist", out var artNameP) ? artNameP.GetString() ?? "Unknown Artist" : "Unknown Artist";
                        string album = item.TryGetProperty("album", out var albP) ? albP.GetString() ?? "" : "";
                        string genre = item.TryGetProperty("genre", out var genreP) ? genreP.GetString() ?? "" : "";
                        string year = item.TryGetProperty("year", out var yrP) ? yrP.ToString() : "";
                        string charter = item.TryGetProperty("charter", out var chP) ? chP.GetString() ?? "Unknown" : "Unknown";
                        string chartContentDisplay = BuildChartContentDisplay(item);

                        DateTime? modifiedTime = null;
                        if (item.TryGetProperty("modifiedTime", out var mtP) && mtP.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(mtP.GetString(), out var parsedDt)) modifiedTime = parsedDt;
                        }

                        var chart = new ChorusChart
                        {
                            Id = item.TryGetProperty("chartId", out var idP) ? idP.ToString() : md5,
                            Md5 = md5,
                            Title = title,
                            Artist = artist,
                            Album = album,
                            Genre = genre,
                            Year = year,
                            Charter = charter,
                            Duration = durationStr,
                            DurationSeconds = songLenSec,
                            DiffGuitar = diffGuitar,
                            DiffBass = diffBass,
                            PreviewStart = previewSec,
                            ModifiedTime = modifiedTime,
                            ChartContentDisplay = chartContentDisplay,
                            DownloadUrl = !string.IsNullOrEmpty(md5) ? $"{FilesUrl}/{md5}.sng" : null,
                            ArtUrl = !string.IsNullOrEmpty(artMd5) ? $"{FilesUrl}/{artMd5}.jpg" : null
                        };

                        charts.Add(chart);
                    }
                }

                return (total, charts);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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

        private static string BuildChartContentDisplay(JsonElement item)
        {
            if (!item.TryGetProperty("notesData", out var notesData) || notesData.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var difficultyByInstrument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (notesData.TryGetProperty("noteCounts", out var noteCounts) && noteCounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var noteCount in noteCounts.EnumerateArray())
                {
                    string instrument = ReadString(noteCount, "instrument");
                    string difficulty = ReadString(noteCount, "difficulty");
                    int count = noteCount.TryGetProperty("count", out var countProp) && countProp.TryGetInt32(out int parsedCount)
                        ? parsedCount
                        : 0;
                    if (count <= 0 || string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(difficulty))
                        continue;

                    if (!difficultyByInstrument.TryGetValue(instrument, out var difficulties))
                        difficultyByInstrument[instrument] = difficulties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    difficulties.Add(difficulty);
                }
            }

            if (notesData.TryGetProperty("instruments", out var instruments) && instruments.ValueKind == JsonValueKind.Array)
            {
                foreach (var instrument in instruments.EnumerateArray())
                {
                    if (instrument.ValueKind != JsonValueKind.String) continue;
                    string name = instrument.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name) && !difficultyByInstrument.ContainsKey(name))
                        difficultyByInstrument[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }

            var instrumentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["guitar"] = "Guitar",
                ["guitar_coop"] = "Co-op Guitar",
                ["rhythm"] = "Rhythm Guitar",
                ["bass"] = "Bass",
                ["drums"] = "Drums",
                ["keys"] = "Keys",
                ["guitarghl"] = "GHL Guitar",
                ["guitar_coop_ghl"] = "GHL Co-op Guitar",
                ["rhythm_ghl"] = "GHL Rhythm Guitar",
                ["bassghl"] = "GHL Bass"
            };
            string[] instrumentOrder =
            {
                "guitar", "bass", "drums", "keys", "rhythm", "guitar_coop",
                "guitarghl", "guitar_coop_ghl", "rhythm_ghl", "bassghl"
            };
            string[] difficultyOrder = { "easy", "medium", "hard", "expert" };
            var parts = new List<string>();
            foreach (string instrument in instrumentOrder)
            {
                if (!difficultyByInstrument.TryGetValue(instrument, out var difficulties)) continue;
                string displayName = instrumentNames.TryGetValue(instrument, out var mappedName) ? mappedName : instrument;
                string available = string.Join("/", difficultyOrder
                    .Where(difficulties.Contains)
                    .Select(difficulty => difficulty switch
                    {
                        "easy" => "E",
                        "medium" => "M",
                        "hard" => "H",
                        "expert" => "X",
                        _ => difficulty
                    }));
                parts.Add(available.Length > 0 ? $"{displayName} {available}" : displayName);
            }

            bool hasVocals = ReadBoolean(notesData, "hasVocals") == true;
            bool hasLyrics = ReadBoolean(notesData, "hasLyrics") == true;
            if (hasVocals && hasLyrics) parts.Add("Vocals + lyrics");
            else if (hasVocals) parts.Add("Vocal chart");
            else if (hasLyrics) parts.Add("Lyrics");

            bool? hasVideo = ReadBoolean(item, "hasVideoBackground");
            if (hasVideo == true) parts.Add("Video background");
            else if (hasVideo == false) parts.Add("No video");

            return parts.Count == 0 ? "Chart details unavailable" : "Chart: " + string.Join(" · ", parts);
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool? ReadBoolean(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)) return null;
            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
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
