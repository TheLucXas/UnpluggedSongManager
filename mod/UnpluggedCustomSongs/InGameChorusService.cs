using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using USM.Core.Parsers;
using USM.Core.Services;

namespace UnpluggedCustomSongs
{
    internal sealed class ChorusResult
    {
        public string Id;
        public string Md5;
        public string ArtMd5;
        public string Title;
        public string Artist;
        public string Charter;
        public int DurationSeconds;
        public int GuitarDifficulty;
    }

    // This runs inside the game. It never needs the Windows USM process, ADB,
    // or a desktop service. Only Chorus HTTPS endpoints are accepted.
    internal static class InGameChorusService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        public const int PageSize = 8;

        public static async Task<(int Total, List<ChorusResult> Charts)> SearchAsync(
            string query, int page, CancellationToken cancellation)
        {
            var payload = new JObject
            {
                ["search"] = string.IsNullOrWhiteSpace(query) ? "*" : query.Trim(),
                ["page"] = Math.Max(1, page),
                ["per_page"] = PageSize,
                ["instrument"] = "guitar",
                ["source"] = "api"
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.enchor.us/search"))
            {
                request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                request.Headers.UserAgent.ParseAdd("USM/1.0 (VR Rhythm Game Mod Tool)");
                using (var response = await Http.SendAsync(request, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                    int total = (int?)body["total"] ?? (int?)body["found"] ?? 0;
                    var charts = new List<ChorusResult>();
                    var data = body["data"] as JArray;
                    if (data != null)
                    {
                        foreach (var item in data)
                        {
                            string md5 = (string)item["md5"] ?? "";
                            if (!IsSafeMd5(md5)) continue;
                            charts.Add(new ChorusResult
                            {
                                Id = (string)item["chartId"] ?? md5,
                                Md5 = md5,
                                ArtMd5 = (string)item["albumArtMd5"] ?? "",
                                Title = (string)item["name"] ?? "Unknown Title",
                                Artist = (string)item["artist"] ?? "Unknown Artist",
                                Charter = (string)item["charter"] ?? "Unknown",
                                DurationSeconds = Math.Max(0, ((int?)item["song_length"] ?? 0) / 1000),
                                GuitarDifficulty = (int?)item["diff_guitar"] ?? -1
                            });
                        }
                    }
                    return (total, charts);
                }
            }
        }

        public static async Task<string> DownloadAndConvertAsync(ChorusResult chart,
            string songsFolder, string temporaryFolder, Action<string> status,
            CancellationToken cancellation)
        {
            if (chart == null || !IsSafeMd5(chart.Md5))
                throw new InvalidDataException("Invalid Chorus chart identifier.");

            string name = SafeName(chart.Artist + " - " + chart.Title + " [" + chart.Md5.Substring(0, 8) + "]");
            string destination = Path.Combine(songsFolder, name + ".usm");
            if (File.Exists(destination))
                throw new IOException("This chart is already installed.");

            string working = Path.Combine(temporaryFolder, "USM_Chorus_" + Guid.NewGuid().ToString("N"));
            string raw = Path.Combine(working, "source");
            Directory.CreateDirectory(raw);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                status("DOWNLOADING CHART");
#if QUEST_IL2CPP
                // Inspect only the SNG directory first. An unplayable stem must
                // not be silently omitted, as that would produce a partial song.
                var sourceNames = await SngDecoder.ListRemoteFileNamesAsync(
                    "https://files.enchor.us/" + chart.Md5 + ".sng", cancellation);
                foreach (string sourceName in sourceNames)
                {
                    string extension = Path.GetExtension(sourceName).ToLowerInvariant();
                    if (!IsAudioExtension(extension) || extension == ".ogg" || extension == ".mp3") continue;
                    string stemName = Path.GetFileNameWithoutExtension(sourceName).ToLowerInvariant();
                    if (stemName == "song" || stemName.StartsWith("sample") || stemName.StartsWith("preview")) continue;
                    throw new NotSupportedException("This chart has " + extension +
                        " audio stems. Convert it in PC USM and sync the .usm to Quest.");
                }
#endif
                var downloadProgress = new Progress<double>(fraction =>
                    status("DOWNLOADING CHART " + (int)(Math.Max(0, Math.Min(1, fraction)) * 100) + "%"));
                await SngDecoder.DecodeRemoteAsync("https://files.enchor.us/" + chart.Md5 + ".sng",
                    raw, downloadProgress, IsNeededSourceFile, cancellation, allowFullDownloadFallback: false);
                NormalizeSourceNames(raw);
                if (FindCover(raw) == null && IsSafeMd5(chart.ArtMd5))
                {
                    try { await DownloadCoverAsync(chart.ArtMd5, Path.Combine(raw, "cover.jpg"), cancellation); }
                    catch (HttpRequestException) { }
                    catch (IOException) { }
                }

                cancellation.ThrowIfCancellationRequested();
                status("PARSING NOTES");
                double duration = ReadIniSeconds(Path.Combine(raw, "song.ini"), "song_length", chart.DurationSeconds);
                var jsonCharts = ChartConversion.GenerateJsonCharts(raw, null, duration,
                    out int sourceDifficulties, out int noteCount, out double coverage, out bool hasLeadGuitar);
                if (jsonCharts.Count == 0 || noteCount == 0)
                    throw new InvalidDataException("This chart has no playable guitar notes.");

                status("PREPARING AUDIO");
#if QUEST_IL2CPP
                var audio = PrepareAudio(raw);
                string coverFolder = raw;
#else
                string processed = Path.Combine(working, "converted");
                Directory.CreateDirectory(processed);
                double previewStart = ReadIniSeconds(Path.Combine(raw, "song.ini"), "preview_start_time", 30);
                await AudioPipeline.ProcessAudioAsync(raw, processed, previewStart, null, cancellation);
                await AudioPipeline.ProcessCoverAsync(raw, processed, cancellation);
                var audio = PrepareConvertedAudio(processed);
                string coverFolder = processed;
#endif
                double delay = Math.Max(2.0, ReadIniSeconds(Path.Combine(raw, "song.ini"), "delay", 0));
                var meta = new JObject
                {
                    ["BandName"] = chart.Artist,
                    ["FileName"] = name,
                    ["InitialDelay"] = delay,
                    ["IsBuiltIn"] = false,
                    ["IsMultiTrack"] = audio.Layout != AudioTrackLayout.SingleTrack,
                    ["AudioLayout"] = audio.Layout == AudioTrackLayout.MultiTrack ? "full-multitrack" :
                        audio.Layout == AudioTrackLayout.HalfTrack ? "two-track-instrumental-vocals" : "single-track",
                    ["AudioStemCount"] = audio.StemCount,
                    ["SourceDifficultyCount"] = sourceDifficulties,
                    ["SourceChartNoteCount"] = noteCount,
                    ["ChartCoverageRatio"] = coverage,
                    ["SongDurationSeconds"] = duration,
                    ["CompatibilityProfileVersion"] = 3,
                    ["IsIdealForUnplugged"] = audio.Layout == AudioTrackLayout.MultiTrack &&
                        new[] { "guitar", "bass", "drums", "vocals" }.All(audio.StemGroups.Contains) &&
                        hasLeadGuitar && sourceDifficulties >= 4 && coverage >= 0.90,
                    ["HasLeadGuitarChart"] = hasLeadGuitar,
                    ["LevelAuthorName"] = chart.Charter,
                    ["SongID"] = "CUSTOM_" + name,
                    ["SongName"] = chart.Title,
                    ["ChorusChartId"] = chart.Id,
                    ["ChorusChartMd5"] = chart.Md5,
                    ["ChartParserVersion"] = JsonGenerator.ParserVersion
                };

                cancellation.ThrowIfCancellationRequested();
                status("PACKAGING SONG");
                Directory.CreateDirectory(songsFolder);
                string staged = Path.Combine(working, name + ".usm");
                using (var archive = ZipFile.Open(staged, ZipArchiveMode.Create))
                {
                    WriteText(archive, "meta.json", meta.ToString());
                    foreach (var pair in jsonCharts)
                    {
                        WriteText(archive, pair.Key, pair.Value);
                        WriteText(archive, Path.GetFileNameWithoutExtension(pair.Key) + ".hand.xml",
                            XmlGenerator.BuildQuestHandXml(pair.Value));
                    }
                    foreach (var pair in audio.Files)
                        archive.CreateEntryFromFile(pair.Value, pair.Key, CompressionLevel.NoCompression);
                    string cover = FindCover(coverFolder) ?? FindCover(raw);
                    if (cover != null)
                        archive.CreateEntryFromFile(cover, Path.GetFileName(cover), CompressionLevel.NoCompression);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(staged, destination);
                status("INSTALLED");
                return destination;
            }
            finally
            {
                try { if (Directory.Exists(working)) Directory.Delete(working, true); } catch { }
            }
        }

        private static (AudioTrackLayout Layout, int StemCount, HashSet<string> StemGroups,
            Dictionary<string, string> Files) PrepareAudio(string raw)
        {
            string[] all = Directory.GetFiles(raw);
            string[] supported = all.Where(IsDirectlyPlayableAudio).ToArray();
            if (supported.Length == 0)
                throw new NotSupportedException("This chart needs audio conversion. Quest import currently accepts OGG Vorbis or MP3.");

            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in supported)
            {
                string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (stem == "sample" || stem == "preview") continue;
                string group = GetStemGroup(stem);
                if (group == null) group = "other";
                string entry = group == "song" ? "song" + Path.GetExtension(file).ToLowerInvariant() :
                    "song-" + group + Path.GetExtension(file).ToLowerInvariant();
                if (mapped.Keys.Any(key => Path.GetFileNameWithoutExtension(key).Equals(Path.GetFileNameWithoutExtension(entry), StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException("This chart has multiple files for one audio stem and needs audio mixing.");
                mapped[entry] = file;
                if (group != "song") groups.Add(group);
            }

            if (mapped.Count == 0)
                throw new NotSupportedException("This chart has no supported song or separated audio stems.");
            if (groups.Count == 0 && !mapped.Keys.Any(key => key.StartsWith("song.", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("This chart has no playable master audio track.");

            AudioTrackLayout layout = groups.Count >= 2
                ? groups.Count == 2 && groups.Contains("vocals") ? AudioTrackLayout.HalfTrack : AudioTrackLayout.MultiTrack
                : AudioTrackLayout.SingleTrack;
            string master = mapped.Keys.FirstOrDefault(key => key.StartsWith("song.", StringComparison.OrdinalIgnoreCase));
            if (groups.Count == 1 && groups.Contains("vocals") && master != null)
            {
                string source = mapped[master];
                mapped["song-guitar" + Path.GetExtension(source).ToLowerInvariant()] = source;
                groups.Add("guitar");
                layout = AudioTrackLayout.HalfTrack;
            }
            else if (groups.Count == 1 && master == null)
            {
                if (groups.Contains("vocals"))
                    throw new NotSupportedException("This chart has only a vocal stem and no playable backing track.");
                string source = mapped.First(pair => pair.Key.StartsWith("song-", StringComparison.OrdinalIgnoreCase)).Value;
                mapped["song" + Path.GetExtension(source).ToLowerInvariant()] = source;
                layout = AudioTrackLayout.SingleTrack;
            }
            if (layout != AudioTrackLayout.SingleTrack && groups.Count == 2 && groups.Contains("vocals") &&
                groups.Contains("other") && mapped.Keys.Any(key => key.StartsWith("song.", StringComparison.OrdinalIgnoreCase)))
            {
                // A separate master song file is a mix; keep the two independent channels.
                layout = AudioTrackLayout.HalfTrack;
            }
            if (layout != AudioTrackLayout.SingleTrack && !mapped.Keys.Any(key => key.StartsWith("song.", StringComparison.OrdinalIgnoreCase)))
            {
                // Menu preview can use a stem. Gameplay uses the independent stems.
                string sample = mapped.First(pair => pair.Key.StartsWith("song-", StringComparison.OrdinalIgnoreCase)).Value;
                mapped["sample" + Path.GetExtension(sample).ToLowerInvariant()] = sample;
            }
            string preview = supported.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals("sample", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(path).Equals("preview", StringComparison.OrdinalIgnoreCase));
            if (preview != null)
            {
                foreach (string key in mapped.Keys.Where(key => key.StartsWith("sample.", StringComparison.OrdinalIgnoreCase)).ToArray())
                    mapped.Remove(key);
                mapped["sample" + Path.GetExtension(preview).ToLowerInvariant()] = preview;
            }
            return (layout, groups.Count == 0 ? 1 : groups.Count, groups, mapped);
        }

#if !QUEST_IL2CPP
        private static (AudioTrackLayout Layout, int StemCount, HashSet<string> StemGroups,
            Dictionary<string, string> Files) PrepareConvertedAudio(string folder)
        {
            var files = Directory.GetFiles(folder, "*.ogg")
                .ToDictionary(Path.GetFileName, path => path, StringComparer.OrdinalIgnoreCase);
            if (!files.ContainsKey("song.ogg"))
                throw new InvalidDataException("Audio conversion did not produce song.ogg.");
            var profile = AudioLayoutDetector.Analyze(files.Keys);
            return (profile.Layout, profile.StemCount, profile.StemGroups, files);
        }
#endif

        private static string GetStemGroup(string stem)
        {
            if (stem == "song") return "song";
            if (stem.StartsWith("song-") || stem.StartsWith("song_")) stem = stem.Substring(5);
            if (stem.StartsWith("guitar") || stem.StartsWith("lead")) return "guitar";
            if (stem.StartsWith("bass") || stem.StartsWith("rhythm")) return "bass";
            if (stem.StartsWith("drum")) return "drums";
            if (stem.StartsWith("vocal")) return "vocals";
            if (stem.StartsWith("other") || stem.StartsWith("keys") || stem.StartsWith("band") ||
                stem.StartsWith("instrumental") || stem.StartsWith("backing")) return "other";
            return null;
        }

        private static bool IsAudioExtension(string extension) =>
            extension == ".ogg" || extension == ".mp3" || extension == ".opus" ||
            extension == ".wav" || extension == ".flac" || extension == ".m4a" ||
            extension == ".aac";

        private static bool IsDirectlyPlayableAudio(string path)
        {
            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return true;
            if (!path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return false;
            // Unity's OGGVORBIS decoder cannot play Opus packets in an Ogg container.
            byte[] header = new byte[128];
            using (var file = File.OpenRead(path))
            {
                int count = file.Read(header, 0, header.Length);
                if (count < 32 || Encoding.ASCII.GetString(header, 0, 4) != "OggS") return false;
                return Encoding.ASCII.GetString(header, 0, count).Contains("vorbis");
            }
        }

        private static string FindCover(string raw)
        {
            string[] names = { "cover.jpg", "cover.jpeg", "cover.png", "album.jpg", "album.jpeg", "album.png" };
            foreach (var name in names)
            {
                string path = Directory.GetFiles(raw).FirstOrDefault(file =>
                    Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (path != null) return path;
            }
            return null;
        }

        private static void NormalizeSourceNames(string directory)
        {
            string[] canonical = { "song.ini", "notes.mid", "notes.midi", "notes.chart", "song.chart" };
            foreach (string path in Directory.GetFiles(directory))
            {
                string name = Path.GetFileName(path);
                string targetName = canonical.FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (targetName == null || name == targetName) continue;
                string target = Path.Combine(directory, targetName);
                if (!File.Exists(target)) File.Move(path, target);
            }
        }

        private static async Task DownloadCoverAsync(string md5, string output, CancellationToken cancellation)
        {
            using (var response = await Http.GetAsync("https://files.enchor.us/" + md5 + ".jpg",
                HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                    return;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] buffer = new byte[16 * 1024];
                    int read;
                    long written = 0;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) > 0)
                    {
                        written += read;
                        if (written > 4 * 1024 * 1024)
                            throw new InvalidDataException("Chorus cover exceeds the permitted size.");
                        await file.WriteAsync(buffer, 0, read, cancellation);
                    }
                }
            }
        }

        private static void WriteText(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(value);
        }

        private static double ReadIniSeconds(string path, string key, double fallback)
        {
            if (!File.Exists(path)) return fallback;
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator < 0 || !line.Substring(0, separator).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (double.TryParse(line.Substring(separator + 1).Trim(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
                    return value / 1000.0;
            }
            return fallback;
        }

        private static bool IsSafeMd5(string md5) => md5 != null && md5.Length == 32 &&
            md5.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');

        private static bool IsNeededSourceFile(string name)
        {
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".ini" || extension == ".chart" || extension == ".mid" ||
                extension == ".midi" || extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                return true;
#if QUEST_IL2CPP
            return extension == ".ogg" || extension == ".mp3";
#else
            return extension == ".ogg" || extension == ".mp3" || extension == ".opus" ||
                extension == ".wav" || extension == ".flac" || extension == ".m4a";
#endif
        }

        private static string SafeName(string value)
        {
            var bad = new HashSet<char>("<>:\"/\\|?*");
            string text = new string((value ?? "Custom Song").Where(c => !bad.Contains(c) && c >= ' ').ToArray()).Trim().TrimEnd('.');
            return text.Length > 120 ? text.Substring(0, 120).TrimEnd() : text.Length == 0 ? "Custom Song" : text;
        }
    }
}
