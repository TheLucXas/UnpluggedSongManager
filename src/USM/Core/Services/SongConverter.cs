using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
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
                    }
                }
            }
            catch { }
            return info;
        }

        public static async Task<bool> ConvertSongAsync(
            string inputPath,
            string outputParentDir,
            IProgress<ConversionProgress>? progress = null,
            Action<string>? log = null,
            CancellationToken ct = default)
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

                        // Locate inner directory containing chart notes or song files
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

                // 2. Validate that chart notes and audio exist before proceeding
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

                // 3. Read song.ini and determine song folder name
                string iniPath = Path.Combine(inputDir, "song.ini");
                var iniInfo = ParseSongIni(iniPath);

                string folderName = SanitizeFolderName($"{iniInfo.Artist} - {iniInfo.Name}");
                outDir = Path.Combine(outputParentDir, folderName);
                Directory.CreateDirectory(outDir);

                log?.Invoke($"--- Starting conversion: {iniInfo.Artist} - {iniInfo.Name} ---");
                progress?.Report(new ConversionProgress(0.15, "Converting audio and stems..."));

                // 4. Audio Conversion (Stems / Downmix)
                bool hasMultiTrack = await AudioPipeline.ProcessAudioAsync(
                    inputDir,
                    outDir,
                    iniInfo.PreviewStart,
                    log,
                    ct
                );

                if (ct.IsCancellationRequested)
                {
                    CleanupOutDir(outDir);
                    return false;
                }

                // 5. Album Cover
                progress?.Report(new ConversionProgress(0.55, "Processing album art..."));
                await AudioPipeline.ProcessCoverAsync(inputDir, outDir, ct);

                if (ct.IsCancellationRequested)
                {
                    CleanupOutDir(outDir);
                    return false;
                }

                // 6. Chart Note Generation (.mid or .chart)
                progress?.Report(new ConversionProgress(0.70, "Generating chart note XML..."));

                if (File.Exists(midPath))
                {
                    log?.Invoke("  [Chart] Detected notes.mid (Standard MIDI format).");
                    var midi = MidiParser.Parse(midPath);
                    XmlGenerator.GenerateAllDifficulties(midi.BpmGrid, midi.TickToSec, midi.Difficulties, midi.TicksPerBeat, outDir, log);
                }
                else if (File.Exists(chartPath))
                {
                    log?.Invoke("  [Chart] Detected notes.chart (Clone Hero text format).");
                    var chart = ChartParser.Parse(chartPath);
                    XmlGenerator.GenerateAllDifficulties(chart.BpmGrid, chart.TickToSec, chart.Difficulties, chart.Resolution, outDir, log);
                }

                // 7. Copy / Write song.ini
                string destIni = Path.Combine(outDir, "song.ini");
                if (File.Exists(iniPath))
                {
                    File.Copy(iniPath, destIni, overwrite: true);
                }
                else
                {
                    File.WriteAllText(destIni, $"[song]\nname = {iniInfo.Name}\nartist = {iniInfo.Artist}\ncharter = {iniInfo.Charter}\n", Encoding.UTF8);
                }

                // 8. Generate data.xml
                string xmlData = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<SongLevelData xmlns:i=""http://www.w3.org/2001/XMLSchema-instance"" xmlns=""http://schemas.datacontract.org/2004/07/"">
  <BandName>{CleanXmlText(iniInfo.Artist)}</BandName>
  <FileName>{folderName}</FileName>
  <InitialDelay>{iniInfo.Delay:F1}</InitialDelay>
  <IsBuiltIn>false</IsBuiltIn>
  <IsMultiTrack>{(hasMultiTrack ? "true" : "false")}</IsMultiTrack>
  <LevelAuthorName>{CleanXmlText(iniInfo.Charter)}</LevelAuthorName>
  <SongID>CUSTOM_{folderName}</SongID>
  <SongName>{CleanXmlText(iniInfo.Name)}</SongName>
</SongLevelData>";

                File.WriteAllText(Path.Combine(outDir, "data.xml"), xmlData, Encoding.UTF8);

                progress?.Report(new ConversionProgress(1.0, "Conversion complete!"));
                log?.Invoke($"[SUCCESS] Song converted and installed: {folderName}");
                return true;
            }
            catch (Exception ex)
            {
                CleanupOutDir(outDir);
                log?.Invoke($"[ERROR] Conversion failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        private static void CleanupOutDir(string? outDir)
        {
            if (!string.IsNullOrEmpty(outDir) && Directory.Exists(outDir))
            {
                try { Directory.Delete(outDir, recursive: true); } catch { }
            }
        }
    }
}
