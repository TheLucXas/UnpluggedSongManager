using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using USM.Core.Parsers;

namespace USM.Core.Services
{
    public class UsmPacker
    {
        public const int CompatibilityProfileVersion = 2;

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
                    string audioLayout = GetAudioLayoutName(audioProfile.Layout);
                    bool hasMultipleTracks = audioProfile.Layout is AudioTrackLayout.HalfTrack or AudioTrackLayout.MultiTrack;
                    bool wroteMeta = false;
                    foreach (var sourceEntry in source.Entries)
                    {
                        if (IsGeneratedChartData(sourceEntry.Name)) continue;

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
                            meta["CompatibilityProfileVersion"] = CompatibilityProfileVersion;
                            meta["IsIdealForUnplugged"] = isIdealForUnplugged;
                            meta["AudioLayout"] = audioLayout;
                            meta["IsMultiTrack"] = hasMultipleTracks;
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
                            ["CompatibilityProfileVersion"] = CompatibilityProfileVersion,
                            ["IsIdealForUnplugged"] = isIdealForUnplugged,
                            ["AudioLayout"] = audioLayout,
                            ["IsMultiTrack"] = hasMultipleTracks
                        };
                        WriteEntry(destination, "meta.json", meta.ToJsonString(), CompressionLevel.Fastest, DateTimeOffset.UtcNow);
                    }

                    foreach (var chart in jsonCharts)
                    {
                        WriteEntry(destination, Path.GetFileName(chart.Key), chart.Value, CompressionLevel.Optimal, DateTimeOffset.UtcNow);
                        WriteEntry(destination, GetQuestHandChartName(chart.Key), XmlGenerator.BuildQuestHandXml(chart.Value), CompressionLevel.Optimal, DateTimeOffset.UtcNow);
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

        private static string GetQuestHandChartName(string jsonName) => Path.GetFileNameWithoutExtension(jsonName) + ".hand.xml";

        private static bool IsGeneratedChartData(string entryName)
        {
            string fileName = Path.GetFileName(entryName);
            return fileName.StartsWith("songdata-", StringComparison.OrdinalIgnoreCase) &&
                (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".hand.xml", StringComparison.OrdinalIgnoreCase));
        }

        // Existing .usm archives can gain the Quest hand mapping from their
        // canonical JSON while syncing, without fetching or reparsing a chart.
        public static int EnsureQuestHandCharts(string usmPath)
        {
            using var source = ZipFile.OpenRead(usmPath);
            var names = new HashSet<string>(source.Entries.Select(entry => Path.GetFileName(entry.Name)), StringComparer.OrdinalIgnoreCase);
            var missing = source.Entries
                .Where(entry => entry.Name.StartsWith("songdata-", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && !names.Contains(GetQuestHandChartName(entry.Name)))
                .ToArray();
            if (missing.Length == 0) return 0;

            string tempPath = usmPath + ".hand.tmp";
            try
            {
                using (var destination = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    foreach (var entry in source.Entries)
                    {
                        var copied = destination.CreateEntry(entry.FullName, GetCompressionLevel(entry.Name));
                        copied.LastWriteTime = entry.LastWriteTime;
                        using var input = entry.Open();
                        using var output = copied.Open();
                        input.CopyTo(output);
                    }
                    foreach (var chart in missing)
                    {
                        using var reader = new StreamReader(chart.Open());
                        WriteEntry(destination, GetQuestHandChartName(chart.Name),
                            XmlGenerator.BuildQuestHandXml(reader.ReadToEnd()), CompressionLevel.Optimal, DateTimeOffset.UtcNow);
                    }
                }
                source.Dispose();
                File.Move(tempPath, usmPath, overwrite: true);
                return missing.Length;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        public static void RefreshAudioMetadata(string usmPath)
        {
            if (!File.Exists(usmPath)) throw new FileNotFoundException("USM archive not found.", usmPath);

            string tempPath = usmPath + ".profile.tmp";
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);

                using (var source = ZipFile.OpenRead(usmPath))
                using (var destination = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    var audioProfile = AudioLayoutDetector.Analyze(source.Entries.Select(entry => entry.Name));
                    var metaEntry = source.Entries.FirstOrDefault(entry =>
                        string.Equals(entry.Name, "meta.json", StringComparison.OrdinalIgnoreCase));
                    JsonObject meta;
                    int sourceDifficultyCount = 0;
                    int sourceNoteCount = 0;
                    double chartCoverageRatio = 0;

                    if (metaEntry != null)
                    {
                        using var reader = new StreamReader(metaEntry.Open());
                        meta = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? new JsonObject();
                        sourceDifficultyCount = ReadMetaInt(meta, "SourceDifficultyCount");
                        sourceNoteCount = ReadMetaInt(meta, "SourceChartNoteCount");
                        chartCoverageRatio = ReadMetaDouble(meta, "ChartCoverageRatio");
                    }
                    else
                    {
                        meta = new JsonObject();
                    }

                    bool isIdealForUnplugged = audioProfile.Layout == AudioTrackLayout.MultiTrack
                        && new[] { "guitar", "bass", "drums", "vocals" }.All(audioProfile.StemGroups.Contains)
                        && sourceDifficultyCount >= 4
                        && sourceNoteCount > 0
                        && chartCoverageRatio >= 0.90;
                    string audioLayout = GetAudioLayoutName(audioProfile.Layout);
                    bool hasMultipleTracks = audioProfile.Layout is AudioTrackLayout.HalfTrack or AudioTrackLayout.MultiTrack;
                    bool wroteMeta = false;

                    foreach (var sourceEntry in source.Entries)
                    {
                        if (string.Equals(sourceEntry.Name, "meta.json", StringComparison.OrdinalIgnoreCase))
                        {
                            meta["AudioStemCount"] = audioProfile.StemCount;
                            meta["CompatibilityProfileVersion"] = CompatibilityProfileVersion;
                            meta["IsIdealForUnplugged"] = isIdealForUnplugged;
                            meta["AudioLayout"] = audioLayout;
                            meta["IsMultiTrack"] = hasMultipleTracks;
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
                        meta["AudioStemCount"] = audioProfile.StemCount;
                        meta["CompatibilityProfileVersion"] = CompatibilityProfileVersion;
                        meta["IsIdealForUnplugged"] = isIdealForUnplugged;
                        meta["AudioLayout"] = audioLayout;
                        meta["IsMultiTrack"] = hasMultipleTracks;
                        WriteEntry(destination, "meta.json", meta.ToJsonString(), CompressionLevel.Fastest, DateTimeOffset.UtcNow);
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

        private static int ReadMetaInt(JsonObject meta, string propertyName)
        {
            if (!meta.TryGetPropertyValue(propertyName, out JsonNode? value) || value == null)
                return 0;
            return int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }

        private static double ReadMetaDouble(JsonObject meta, string propertyName)
        {
            if (!meta.TryGetPropertyValue(propertyName, out JsonNode? value) || value == null)
                return 0;
            return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? Math.Clamp(parsed, 0, 1) : 0;
        }

        private static string GetAudioLayoutName(AudioTrackLayout layout)
        {
            return layout switch
            {
                AudioTrackLayout.HalfTrack => "two-track-instrumental-vocals",
                AudioTrackLayout.MultiTrack => "full-multitrack",
                AudioTrackLayout.SingleTrack => "single-track",
                _ => "unknown"
            };
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
