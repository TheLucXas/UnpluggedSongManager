using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Models;
using USM.Core.Parsers;

namespace USM.Core.Services
{
    public class SongConverter
    {
        public static string SanitizeFolderName(string name)
        {
            string invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            string pattern = $"[{Regex.Escape(invalid)}]";
            string clean = Regex.Replace(name, pattern, "").Trim();
            return string.IsNullOrEmpty(clean) ? "Custom Song" : clean;
        }

        public static string CleanXmlText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string noTags = Regex.Replace(text, @"<[^>]*>", "").Trim();
            return SecurityElement.Escape(noTags) ?? noTags;
        }
        
        public static string CleanJsonText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string noTags = Regex.Replace(text, @"<[^>]*>", "").Trim();
            return noTags.Replace("\"", "\\\"");
        }

        public static SongIni ParseSongIni(string iniPath)
        {
            var info = new SongIni();
            if (!File.Exists(iniPath)) return info;

            try
            {
                foreach (var rawLine in File.ReadLines(iniPath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("[") || !line.Contains("=")) continue;

                    int idx = line.IndexOf('=');
                    string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    string val = line.Substring(idx + 1).Trim().Trim('"');

                    switch (key)
                    {
                        case "name":
                            if (!string.IsNullOrEmpty(val)) info.Name = Regex.Replace(val, @"<[^>]*>", "").Trim();
                            break;
                        case "artist":
                            if (!string.IsNullOrEmpty(val)) info.Artist = Regex.Replace(val, @"<[^>]*>", "").Trim();
                            break;
                        case "charter":
                            if (!string.IsNullOrEmpty(val)) info.Charter = Regex.Replace(val, @"<[^>]*>", "").Trim();
                            break;
                        case "preview_start_time":
                            if (double.TryParse(val, out double pst)) info.PreviewStart = pst / 1000.0;
                            break;
                        case "delay":
                            if (double.TryParse(val, out double del)) info.Delay = Math.Max(2.0, del / 1000.0);
                            break;
                        case "song_length":
                            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthMs) && lengthMs > 0)
                                info.DurationSeconds = lengthMs / 1000.0;
                            break;
                    }
                }
            }
            catch { }
            return info;
        }

        public static Dictionary<string, string> GenerateJsonCharts(string inputDir, Action<string>? log = null)
        {
            return GenerateJsonCharts(inputDir, log, 0, out _, out _, out _);
        }

        public static Dictionary<string, string> GenerateJsonCharts(
            string inputDir,
            Action<string>? log,
            double songDurationSeconds,
            out int sourceDifficultyCount,
            out int sourceNoteCount,
            out double chartCoverageRatio)
        {
            sourceDifficultyCount = 0;
            sourceNoteCount = 0;
            chartCoverageRatio = 0;
            string midPath = Path.Combine(inputDir, "notes.mid");
            string chartPath = Path.Combine(inputDir, "notes.chart");
            if (!File.Exists(chartPath)) chartPath = Path.Combine(inputDir, "song.chart");

            var candidates = new List<(string Name, List<ChartParser.BpmEvent> BpmGrid, Func<long, double> TickToSec,
                Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> Difficulties, int TicksPerBeat)>();

            if (File.Exists(midPath))
            {
                try
                {
                    log?.Invoke("  [Chart] Reading notes.mid (Standard MIDI format).");
                    var midi = MidiParser.Parse(midPath);
                    log?.Invoke($"  [Chart] MIDI guitar track '{midi.SelectedTrackName}' contains {midi.SelectedTrackPlayableNoteCount} notes in supported difficulty ranges.");
                    candidates.Add(("notes.mid", midi.BpmGrid, midi.TickToSec, midi.Difficulties, midi.TicksPerBeat));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [Chart] Could not parse notes.mid: {ex.Message}");
                }
            }

            if (File.Exists(chartPath))
            {
                try
                {
                    log?.Invoke($"  [Chart] Reading {Path.GetFileName(chartPath)} (Clone Hero text format).");
                    var chart = ChartParser.Parse(chartPath);
                    if (chart.IgnoredNonPlayableNoteEvents > 0)
                    {
                        log?.Invoke($"  [Chart] Ignored {chart.IgnoredNonPlayableNoteEvents} non-playable note events (HOPO/tap markers or unsupported lanes).");
                    }
                    candidates.Add((Path.GetFileName(chartPath), chart.BpmGrid, chart.TickToSec, chart.Difficulties, chart.Resolution));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [Chart] Could not parse {Path.GetFileName(chartPath)}: {ex.Message}");
                }
            }

            var selected = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    DifficultyCount = candidate.Difficulties.Count(entry => entry.Value.Values.Any(group => group != null && group.Count > 0)),
                    NoteCount = candidate.Difficulties.Values.Sum(notes => notes.Values.Where(group => group != null).Sum(group => group.Count)),
                    IsTextChart = candidate.Name.EndsWith(".chart", StringComparison.OrdinalIgnoreCase),
                    NoteTimes = candidate.Difficulties.Values
                        .SelectMany(notes => notes.Keys)
                        .Select(candidate.TickToSec)
                        .Where(time => double.IsFinite(time) && time >= 0)
                        .ToList()
                })
                .Select(candidate => new
                {
                    candidate.Candidate,
                    candidate.DifficultyCount,
                    candidate.NoteCount,
                    candidate.IsTextChart,
                    candidate.NoteTimes,
                    FirstNoteTime = candidate.NoteTimes.Count > 0 ? candidate.NoteTimes.Min() : 0,
                    LastNoteTime = candidate.NoteTimes.Count > 0 ? candidate.NoteTimes.Max() : 0,
                    Coverage = songDurationSeconds > 0 && candidate.NoteTimes.Count > 0
                        ? Math.Clamp((candidate.NoteTimes.Max() - candidate.NoteTimes.Min()) / songDurationSeconds, 0, 1)
                        : 0,
                    EndCoverage = songDurationSeconds > 0 && candidate.NoteTimes.Count > 0
                        ? Math.Clamp(candidate.NoteTimes.Max() / songDurationSeconds, 0, 1)
                        : 0
                })
                .Where(candidate => candidate.NoteCount > 0)
                // Prefer the chart that spans the song. Chorus packages can
                // contain auxiliary/truncated MIDI and text charts; choosing
                // solely by difficulty count or note count hid long openings
                // (for example, charts whose first note was around 139 seconds).
                .OrderByDescending(candidate => songDurationSeconds > 0 ? candidate.Coverage : 0)
                .ThenByDescending(candidate => songDurationSeconds > 0 ? candidate.EndCoverage : 0)
                .ThenByDescending(candidate => candidate.DifficultyCount)
                .ThenByDescending(candidate => candidate.NoteCount)
                .ThenByDescending(candidate => candidate.IsTextChart)
                .FirstOrDefault();

            if (selected == null)
                throw new FileNotFoundException("No playable guitar notes were found in notes.chart, song.chart, or notes.mid.");

            sourceDifficultyCount = Math.Min(4, selected.DifficultyCount);
            sourceNoteCount = selected.NoteCount;
            double firstNoteTime = selected.FirstNoteTime;
            double lastNoteTime = selected.LastNoteTime;
            chartCoverageRatio = selected.Coverage;

            log?.Invoke($"  [Chart] Using {selected.Candidate.Name}: {selected.DifficultyCount} populated source difficulties, {selected.NoteCount} note events; range {firstNoteTime:F1}-{lastNoteTime:F1}s of {songDurationSeconds:F1}s ({chartCoverageRatio:P0} coverage).");
            if (songDurationSeconds > 0 && (chartCoverageRatio < 0.50 || firstNoteTime > songDurationSeconds * 0.25))
                log?.Invoke("  [ChartWarning] The best available chart has suspiciously low song coverage or a late first note. The source chart may be incomplete; check the generated first-note time before playing.");
            return JsonGenerator.GenerateAllDifficultiesJson(
                selected.Candidate.BpmGrid,
                selected.Candidate.TickToSec,
                selected.Candidate.Difficulties,
                selected.Candidate.TicksPerBeat,
                log,
                songDurationSeconds);
        }
        
        public static void CleanupOutDir(string? outDir)
        {
            try { if (outDir != null && Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }

        public static async Task<bool> ConvertSongAsync(
            string inputPath,
            string outputParentDir,
            IProgress<ConversionProgress>? progress = null,
            Action<string>? log = null,
            CancellationToken ct = default,
            string? sourceChartId = null,
            string? sourceChartMd5 = null)
        {
            string? tempDir = null;
            string? outDir = null;
            string inputDir = inputPath;

            try
            {
                // 1. Unpack input if compressed (.sng or .zip)
                if (File.Exists(inputPath))
                {
                    string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                    if (ext != ".sng" && ext != ".zip")
                    {
                        throw new NotSupportedException($"File '{Path.GetFileName(inputPath)}' is not a supported chart archive (.sng or .zip).");
                    }

                    tempDir = Path.Combine(Path.GetTempPath(), $"usm_conv_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);

                    if (ext == ".sng")
                    {
                        log?.Invoke($"  [SNG] Unpacking '{Path.GetFileName(inputPath)}'...");
                        progress?.Report(new ConversionProgress(0.05, "Extracting .sng file..."));
                        SngDecoder.Decode(inputPath, tempDir);
                        inputDir = tempDir;
                    }
                    else if (ext == ".zip")
                    {
                        log?.Invoke($"  [ZIP] Unpacking '{Path.GetFileName(inputPath)}'...");
                        progress?.Report(new ConversionProgress(0.05, "Extracting .zip archive..."));
                        ZipFile.ExtractToDirectory(inputPath, tempDir, overwriteFiles: true);

                        inputDir = tempDir;
                        foreach (var dir in Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories))
                        {
                            if (Directory.GetFiles(dir).Any(f =>
                            {
                                string fn = Path.GetFileName(f).ToLowerInvariant();
                                return fn.StartsWith("notes.") || fn.StartsWith("song.");
                            }))
                            {
                                inputDir = dir;
                                break;
                            }
                        }
                    }
                }
                else if (!Directory.Exists(inputPath))
                {
                    throw new FileNotFoundException($"Specified path does not exist: {inputPath}");
                }

                // 2. Validate
                string midPath = Path.Combine(inputDir, "notes.mid");
                string chartPath = Path.Combine(inputDir, "notes.chart");
                if (!File.Exists(chartPath)) chartPath = Path.Combine(inputDir, "song.chart");

                if (!File.Exists(midPath) && !File.Exists(chartPath))
                {
                    throw new FileNotFoundException("No chart notes file (notes.chart or notes.mid) found in the song package.");
                }

                var audioExts = new[] { ".ogg", ".mp3", ".wav", ".opus", ".m4a" };
                bool hasAudio = Directory.GetFiles(inputDir)
                    .Any(f => audioExts.Contains(Path.GetExtension(f).ToLowerInvariant()));

                if (!hasAudio)
                {
                    throw new FileNotFoundException("No playable audio track files found in the song package.");
                }

                // 3. Read song.ini
                string iniPath = Path.Combine(inputDir, "song.ini");
                var iniInfo = ParseSongIni(iniPath);

                string folderName = SanitizeFolderName($"{iniInfo.Artist} - {iniInfo.Name}");
                outDir = Path.Combine(Path.GetTempPath(), $"usm_out_{Guid.NewGuid():N}");
                Directory.CreateDirectory(outDir);

                log?.Invoke($"--- Starting conversion: {iniInfo.Artist} - {iniInfo.Name} ---");
                progress?.Report(new ConversionProgress(0.15, "Converting audio and stems..."));

                // 4. Audio Conversion (Stems / Downmix) -> Outputs .ogg
                var audioResult = await AudioPipeline.ProcessAudioAsync(
                    inputDir,
                    outDir,
                    iniInfo.PreviewStart,
                    log,
                    ct
                );

                if (ct.IsCancellationRequested) { CleanupOutDir(outDir); return false; }

                // 5. Album Cover -> Outputs cover.jpg
                progress?.Report(new ConversionProgress(0.55, "Processing album art..."));
                await AudioPipeline.ProcessCoverAsync(inputDir, outDir, ct);

                if (ct.IsCancellationRequested) { CleanupOutDir(outDir); return false; }

                // 6. Chart Note Generation -> Outputs .json
                progress?.Report(new ConversionProgress(0.70, "Generating USM 2.0 chart notes..."));

                Dictionary<string, string> jsonCharts = GenerateJsonCharts(inputDir, log, iniInfo.DurationSeconds,
                    out int sourceDifficultyCount, out int sourceNoteCount, out double chartCoverageRatio);
                
                if (jsonCharts != null)
                {
                    foreach (var kvp in jsonCharts)
                    {
                        File.WriteAllText(Path.Combine(outDir, kvp.Key), kvp.Value, Encoding.UTF8);
                        string handFile = Path.GetFileNameWithoutExtension(kvp.Key) + ".hand.xml";
                        File.WriteAllText(Path.Combine(outDir, handFile), XmlGenerator.BuildQuestHandXml(kvp.Value), Encoding.UTF8);
                    }
                    log?.Invoke($"  [Chart] Generated {jsonCharts.Count} JSON difficulty files and native Quest hand mappings.");
                }

                var outputAudio = AudioLayoutDetector.Analyze(Directory.GetFiles(outDir)
                    .Select(path => Path.GetFileName(path) ?? string.Empty));
                bool isIdealForUnplugged = outputAudio.Layout == AudioTrackLayout.MultiTrack
                    && new[] { "guitar", "bass", "drums", "vocals" }.All(outputAudio.StemGroups.Contains)
                    && sourceDifficultyCount >= 4
                    && sourceNoteCount > 0
                    && chartCoverageRatio >= 0.90;
                bool hasPackagedMultipleTracks = outputAudio.Layout is AudioTrackLayout.HalfTrack or AudioTrackLayout.MultiTrack;
                string packagedAudioLayout = outputAudio.Layout switch
                {
                    AudioTrackLayout.HalfTrack => "two-track-instrumental-vocals",
                    AudioTrackLayout.MultiTrack => "full-multitrack",
                    AudioTrackLayout.SingleTrack => "single-track",
                    _ => "unknown"
                };
                log?.Invoke($"  [Audio] Packaged layout: {packagedAudioLayout}; {outputAudio.StemCount} independent audio channel(s): {string.Join(", ", outputAudio.StemGroups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase))}.");

                // 7. Generate meta.json
                string metaJson = JsonSerializer.Serialize(new
                {
                    BandName = Regex.Replace(iniInfo.Artist ?? string.Empty, @"<[^>]*>", "").Trim(),
                    FileName = folderName,
                    InitialDelay = iniInfo.Delay,
                    IsBuiltIn = false,
                    IsMultiTrack = hasPackagedMultipleTracks,
                    AudioLayout = packagedAudioLayout,
                    AudioStemCount = outputAudio.StemCount,
                    SourceDifficultyCount = sourceDifficultyCount,
                    SourceChartNoteCount = sourceNoteCount,
                    ChartCoverageRatio = chartCoverageRatio,
                    SongDurationSeconds = iniInfo.DurationSeconds,
                    CompatibilityProfileVersion = UsmPacker.CompatibilityProfileVersion,
                    IsIdealForUnplugged = isIdealForUnplugged,
                    LevelAuthorName = Regex.Replace(iniInfo.Charter ?? string.Empty, @"<[^>]*>", "").Trim(),
                    SongID = $"CUSTOM_{folderName}",
                    SongName = Regex.Replace(iniInfo.Name ?? string.Empty, @"<[^>]*>", "").Trim(),
                    ChorusChartId = sourceChartId ?? string.Empty,
                    ChorusChartMd5 = sourceChartMd5 ?? string.Empty,
                    ChartParserVersion = JsonGenerator.ParserVersion
                });

                // 8. Pack to .usm
                progress?.Report(new ConversionProgress(0.90, "Packaging .usm archive..."));
                string finalUsmPath = Path.Combine(outputParentDir, $"{folderName}.usm");
                UsmPacker.Pack(outDir, finalUsmPath, metaJson);

                progress?.Report(new ConversionProgress(1.0, "Conversion complete!"));
                log?.Invoke($"[SUCCESS] Song packaged to: {folderName}.usm");
                
                CleanupOutDir(outDir);
                if (tempDir != null) CleanupOutDir(tempDir);
                return true;
            }
            catch (Exception ex)
            {
                CleanupOutDir(outDir);
                if (tempDir != null) CleanupOutDir(tempDir);
                log?.Invoke($"[ERROR] Conversion failed: {ex.Message}");
                return false;
            }
        }
    }
}
