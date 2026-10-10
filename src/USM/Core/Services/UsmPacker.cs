using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace USM.Core.Services
{
    public class UsmPacker
    {
        public static void Pack(string inputDir, string outputFile, string metaJsonContent)
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);

            using var archive = ZipFile.Open(outputFile, ZipArchiveMode.Create);

            // 1. Write meta.json first so it can be streamed quickly by the Quest Mod
            var metaEntry = archive.CreateEntry("meta.json", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(metaEntry.Open()))
            {
                writer.Write(metaJsonContent);
            }

            // 2. Add all OGG, JPG, and JSON files
            foreach (var file in Directory.GetFiles(inputDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".ogg" || ext == ".jpg" || ext == ".json" || ext == ".xml")
                {
                    string name = Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, name, ext == ".ogg" || ext == ".jpg" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                }
            }
        }

        public static void Unpack(string inputFile, string outputDir)
        {
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
            using var archive = ZipFile.OpenRead(inputFile);
            foreach (var entry in archive.Entries)
            {
                string destFile = Path.Combine(outputDir, entry.FullName);
                entry.ExtractToFile(destFile, true);
            }
        }

        public static void ReplaceChartData(
            string usmPath,
            IReadOnlyDictionary<string, string> jsonCharts,
            string chorusChartId,
            string chorusChartMd5,
            int chartParserVersion,
            int sourceDifficultyCount,
            int sourceNoteCount,
            double chartCoverageRatio,
            double songDurationSeconds)
        {
            if (!File.Exists(usmPath)) throw new FileNotFoundException("USM archive not found.", usmPath);
            if (jsonCharts == null || jsonCharts.Count == 0)
                throw new InvalidDataException("The updated chart parser did not generate any difficulty charts.");

            string tempPath = usmPath + ".resync.tmp";
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);

                using (var source = ZipFile.OpenRead(usmPath))
                using (var destination = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    var audioProfile = AudioLayoutDetector.Analyze(source.Entries.Select(entry => entry.Name));
                    bool isIdealForUnplugged = audioProfile.Layout == AudioTrackLayout.MultiTrack
                        && new[] { "guitar", "bass", "drums", "vocals" }.All(audioProfile.StemGroups.Contains)
                        && sourceDifficultyCount >= 4
                        && sourceNoteCount > 0
                        && chartCoverageRatio >= 0.90;
                    bool wroteMeta = false;
                    foreach (var sourceEntry in source.Entries)
                    {
                        if (IsGeneratedChartJson(sourceEntry.Name)) continue;

                        if (string.Equals(sourceEntry.Name, "meta.json", StringComparison.OrdinalIgnoreCase))
                        {
                            JsonObject meta;
                            using (var reader = new StreamReader(sourceEntry.Open()))
                            {
                                meta = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? new JsonObject();
                            }

                            meta["ChorusChartId"] = chorusChartId;
                            meta["ChorusChartMd5"] = chorusChartMd5;
                            meta["ChartParserVersion"] = chartParserVersion;
                            meta["SourceDifficultyCount"] = sourceDifficultyCount;
                            meta["SourceChartNoteCount"] = sourceNoteCount;
                            meta["ChartCoverageRatio"] = Math.Clamp(chartCoverageRatio, 0, 1);
                            meta["SongDurationSeconds"] = Math.Max(0, songDurationSeconds);
                            meta["AudioStemCount"] = audioProfile.StemCount;
                            meta["IsIdealForUnplugged"] = isIdealForUnplugged;
                            if (meta["AudioLayout"] == null)
                            {
                                meta["AudioLayout"] = audioProfile.Layout switch
                                {
                                    AudioTrackLayout.HalfTrack => "two-track-instrumental-vocals",
                                    AudioTrackLayout.MultiTrack => "full-multitrack",
                                    AudioTrackLayout.SingleTrack => "single-track",
                                    _ => "unknown"
                                };
                            }
                            WriteEntry(destination, sourceEntry.FullName, meta.ToJsonString(), CompressionLevel.Fastest, sourceEntry.LastWriteTime);
                            wroteMeta = true;
                            continue;
                        }

                        var copied = destination.CreateEntry(sourceEntry.FullName, GetCompressionLevel(sourceEntry.Name));
                        copied.LastWriteTime = sourceEntry.LastWriteTime;
                        using var input = sourceEntry.Open();
                        using var output = copied.Open();
                        input.CopyTo(output);
                    }

                    if (!wroteMeta)
                    {
                        var meta = new JsonObject
                        {
                            ["ChorusChartId"] = chorusChartId,
                            ["ChorusChartMd5"] = chorusChartMd5,
                            ["ChartParserVersion"] = chartParserVersion,
                            ["SourceDifficultyCount"] = sourceDifficultyCount,
                            ["SourceChartNoteCount"] = sourceNoteCount,
                            ["ChartCoverageRatio"] = Math.Clamp(chartCoverageRatio, 0, 1),
                            ["SongDurationSeconds"] = Math.Max(0, songDurationSeconds),
                            ["AudioStemCount"] = audioProfile.StemCount,
                            ["IsIdealForUnplugged"] = isIdealForUnplugged,
                            ["AudioLayout"] = audioProfile.Layout switch
                            {
                                AudioTrackLayout.HalfTrack => "two-track-instrumental-vocals",
                                AudioTrackLayout.MultiTrack => "full-multitrack",
                                AudioTrackLayout.SingleTrack => "single-track",
                                _ => "unknown"
                            }
                        };
                        WriteEntry(destination, "meta.json", meta.ToJsonString(), CompressionLevel.Fastest, DateTimeOffset.UtcNow);
                    }

                    foreach (var chart in jsonCharts)
                    {
                        WriteEntry(destination, Path.GetFileName(chart.Key), chart.Value, CompressionLevel.Optimal, DateTimeOffset.UtcNow);
                    }
                }

                File.Move(tempPath, usmPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        private static bool IsGeneratedChartJson(string entryName)
        {
            string fileName = Path.GetFileName(entryName);
            return fileName.StartsWith("songdata-", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        private static CompressionLevel GetCompressionLevel(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;
        }

        private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel compression, DateTimeOffset lastWrite)
        {
            var entry = archive.CreateEntry(name, compression);
            entry.LastWriteTime = lastWrite;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
