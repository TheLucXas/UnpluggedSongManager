using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace USM.Core.Parsers
{
    /// <summary>
    /// Generates the final hand-tracking and controller note tracks when a .usm is
    /// built. The Quest mod only selects and loads a track; it does not derive one
    /// input format from the other while the song is playing.
    /// </summary>
    public static class JsonGenerator
    {
        // Bump when chart parsing or generated note semantics change. Stored in
        // meta.json so the PC library can selectively regenerate outdated charts.
        public const int ParserVersion = 5;

        private static readonly Dictionary<int, (int Tier, string[] Fingers)> FingerMap = new()
        {
            [0] = (0, new[] { "Index" }),
            [1] = (0, new[] { "Index", "Middle" }),
            [2] = (1, new[] { "Index", "Middle", "Ring" }),
            [3] = (2, new[] { "Index", "Middle", "Ring", "Pinky" }),
            [4] = (3, new[] { "Index", "Middle", "Ring", "Pinky" }),
            [7] = (0, new[] { "Index" })
        };

        private static readonly Dictionary<string, (int Tier, string[] Fingers)> ChordMap = new()
        {
            ["0,1"] = (0, new[] { "Index", "Middle" }),
            ["0,2"] = (0, new[] { "Index", "Ring" }),
            ["1,2"] = (1, new[] { "Index", "Middle" }),
            ["1,3"] = (1, new[] { "Index", "Ring" }),
            ["2,3"] = (2, new[] { "Index", "Middle" }),
            ["2,4"] = (2, new[] { "Index", "Ring" }),
            ["3,4"] = (3, new[] { "Index", "Middle" }),
            ["0,1,2"] = (0, new[] { "Index", "Middle", "Ring" }),
            ["1,2,3"] = (1, new[] { "Index", "Middle", "Ring" }),
            ["2,3,4"] = (2, new[] { "Index", "Middle", "Ring" })
        };

        private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        public static string BuildSongJson(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            int ticksPerBeat,
            string outputDifficulty,
            Action<string>? log = null)
        {
            bpmGrid ??= new List<ChartParser.BpmEvent>();
            notesByTick ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
            tickToSec ??= _ => 0.0;

            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            long lastTick = notesByTick.Count > 0 ? notesByTick.Keys.Last() : 0;
            double totalDuration = Math.Max(0, tickToSec(lastTick)) + 10.0;
            bool superEasyDifficulty = outputDifficulty.Equals("songdata-SuperEasy.json", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder(Math.Max(1024, notesByTick.Count * 140));
            sb.Append("{\"SchemaVersion\":2,")
              .Append("\"Difficulty\":\"").Append(outputDifficulty).Append("\",")
              .Append("\"BPM\":").Append(initialBpm).Append(',')
              .Append("\"Duration\":").Append(Number(totalDuration)).Append(',')
              .Append("\"BPMGrid\":[");

            for (int i = 0; i < bpmGrid.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var bpm = bpmGrid[i];
                sb.Append("{\"Time\":").Append(Number(bpm.TimeSec))
                  .Append(",\"BPM\":").Append(Number(bpm.Bpm)).Append('}');
            }

            sb.Append("],\"HandNotes\":[");
            bool firstHand = true;
            bool firstController = true;
            var controllerNotes = new StringBuilder();
            controllerNotes.Append("],\"ControllerNotes\":[");
            var tierCounts = new SortedDictionary<int, int>();
            var controllerButtonCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

            double previousTime = -999.0;
            int previousTier = -1;

            foreach (var tickEntry in notesByTick)
            {
                if (tickEntry.Value == null || tickEntry.Value.Count == 0)
                    continue;

                long tick = tickEntry.Key;
                double noteTime = tickToSec(tick);
                var lanes = tickEntry.Value
                    .Select(note => note.Lane)
                    .Where(ChartParser.IsPlayableGuitarLane)
                    .Distinct()
                    .OrderBy(lane => lane)
                    .ToList();
                if (lanes.Count == 0)
                    continue;

                long maxDurationTicks = tickEntry.Value.Max(note => note.DurationTicks);
                double noteDuration = maxDurationTicks > (ticksPerBeat / 2)
                    ? Math.Max(0.0, tickToSec(tick + maxDurationTicks) - noteTime)
                    : 0.0;

                int tier;
                string[] fingers;
                if (lanes.Count == 1)
                {
                    if (!FingerMap.TryGetValue(lanes[0], out var fingerMapping))
                        fingerMapping = (0, new[] { "Index" });
                    (tier, fingers) = fingerMapping;
                }
                else
                {
                    string chordKey = string.Join(",", lanes);
                    if (!ChordMap.TryGetValue(chordKey, out var chordMapping))
                        chordMapping = (Math.Min(3, lanes.Last()), new[] { "Index", "Middle", "Ring" });
                    (tier, fingers) = chordMapping;
                }

                // The PC game's shipped chart catalog uses Tier 1 and Tier 2
                // for SuperEasy. Easy uses Tier 0-3 in most official songs, so
                // it must not be clamped with SuperEasy.
                if (superEasyDifficulty)
                {
                    tier = tier <= 1 ? 1 : 2;
                }
                tierCounts[tier] = tierCounts.TryGetValue(tier, out int tierCount) ? tierCount + 1 : 1;

                bool isPullOff = noteTime - previousTime < 0.22 && previousTier != tier;

                if (!firstHand) sb.Append(',');
                sb.Append("{\"Time\":").Append(Number(noteTime))
                  .Append(",\"Duration\":").Append(Number(noteDuration))
                  .Append(",\"Tier\":").Append(tier)
                  .Append(",\"Fingers\":[");
                for (int i = 0; i < fingers.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(fingers[i]).Append('"');
                }
                sb.Append("],\"IsPullOff\":").Append(isPullOff ? "true" : "false").Append('}');
                firstHand = false;

                GetControllerButtons(lanes, out bool trigger, out bool grip);
                string buttonsKey = $"{(trigger ? 1 : 0)}{(grip ? 1 : 0)}";
                controllerButtonCounts[buttonsKey] = controllerButtonCounts.TryGetValue(buttonsKey, out int buttonCount)
                    ? buttonCount + 1
                    : 1;
                if (!firstController) controllerNotes.Append(',');
                controllerNotes.Append("{\"Time\":").Append(Number(noteTime))
                    .Append(",\"Duration\":").Append(Number(noteDuration))
                    .Append(",\"Tier\":").Append(tier)
                    .Append(",\"Buttons\":[").Append(trigger ? "true" : "false").Append(',')
                    .Append(grip ? "true" : "false").Append(']')
                    // Clone Hero charts encode fret lanes, not Unplugged's guitar
                    // rotation gestures. Inferring a rotation from lane 4 (orange)
                    // generated false arrows on otherwise ordinary notes.
                    .Append(",\"ChordRotation\":0")
                    .Append(",\"Intensity\":0}");
                firstController = false;

                previousTime = noteTime;
                previousTier = tier;
            }

            sb.Append(controllerNotes).Append("]}");
            log?.Invoke($"  [ChartDiag] {outputDifficulty}: tiers=[{string.Join(",", tierCounts.Select(pair => $"{pair.Key}:{pair.Value}"))}], controllerButtons[trigger,grip]=[{string.Join(",", controllerButtonCounts.Select(pair => $"{pair.Key}:{pair.Value}"))}].");
            return sb.ToString();
        }

        private static void GetControllerButtons(List<int> lanes, out bool trigger, out bool grip)
        {
            trigger = false;
            grip = false;

            if (lanes.Count == 1)
            {
                int lane = lanes[0];
                if (lane is 0 or 1) trigger = true;
                else if (lane is 2 or 3) grip = true;
                else if (lane == 4) grip = true;
                else trigger = true;
                return;
            }

            bool hasLower = lanes.Any(lane => lane is 0 or 1);
            bool hasUpper = lanes.Any(lane => lane is 2 or 3 or 4);
            trigger = hasLower;
            grip = hasUpper;
        }

        public static Dictionary<string, string> GenerateAllDifficultiesJson(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> difficulties,
            int ticksPerBeat,
            Action<string>? log = null)
        {
            var outputFiles = new[]
            {
                "songdata-SuperEasy.json",
                "songdata-Easy.json",
                "songdata-Normal.json",
                "songdata-Hard.json"
            };
            bool hasNativeSuperEasy = difficulties.TryGetValue("SuperEasySingle", out var nativeSuperEasyNotes)
                && HasNotes(nativeSuperEasyNotes);
            string[] sourceDifficulties = hasNativeSuperEasy
                ? new[] { "SuperEasySingle", "EasySingle", "MediumSingle", "HardSingle" }
                : new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" };
            double[] singleSourceKeepRatios = { 0.60, 0.75, 0.90, 1.0 };

            difficulties ??= new Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>>(StringComparer.OrdinalIgnoreCase);
            var availableSources = sourceDifficulties
                .Select((name, rank) =>
                {
                    difficulties.TryGetValue(name, out var notes);
                    return (Name: name, Rank: rank, Notes: notes);
                })
                .Where(source => HasNotes(source.Notes))
                .ToList();

            log?.Invoke(availableSources.Count == 0
                ? "  [Chart] No playable guitar difficulty sections were found."
                : "  [Chart] Source difficulties: " + string.Join(", ", availableSources.Select(source =>
                    $"{source.Name} ({CountNotes(source.Notes)} notes)")));
            if (hasNativeSuperEasy && difficulties.TryGetValue("ExpertSingle", out var expertNotes) && HasNotes(expertNotes))
                log?.Invoke("  [Chart] The fifth source tier (ExpertSingle) is omitted because USM writes four playable difficulty files.");

            var results = new Dictionary<string, string>();
            for (int targetRank = 0; targetRank < outputFiles.Length; targetRank++)
            {
                SortedDictionary<long, List<ChartParser.NoteEvent>> selectedNotes;
                string origin;

                if (availableSources.Count == 0)
                {
                    selectedNotes = new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                    origin = "no source notes";
                }
                else if (availableSources.Count == 1)
                {
                    selectedNotes = ThinNotes(availableSources[0].Notes!, singleSourceKeepRatios[targetRank]);
                    origin = $"adapted from {availableSources[0].Name} at {singleSourceKeepRatios[targetRank]:P0} density";
                }
                else
                {
                    var anchors = GetDifficultyAnchors(availableSources);
                    var exactAnchor = anchors.FirstOrDefault(anchor => anchor.TargetRank == targetRank);
                    if (exactAnchor.Notes != null)
                    {
                        selectedNotes = CloneNotes(exactAnchor.Notes);
                        origin = $"source {exactAnchor.SourceName}";
                    }
                    else
                    {
                        var lower = anchors.Where(anchor => anchor.TargetRank < targetRank).OrderByDescending(anchor => anchor.TargetRank).FirstOrDefault();
                        var upper = anchors.Where(anchor => anchor.TargetRank > targetRank).OrderBy(anchor => anchor.TargetRank).FirstOrDefault();

                        if (lower.Notes != null && upper.Notes != null)
                        {
                            double fraction = (targetRank - lower.TargetRank) / (double)(upper.TargetRank - lower.TargetRank);
                            selectedNotes = BlendNotes(lower.Notes, upper.Notes, fraction);
                            origin = $"interpolated between {lower.SourceName} and {upper.SourceName} ({fraction:P0})";
                        }
                        else if (lower.Notes != null)
                        {
                            double keepRatio = singleSourceKeepRatios[targetRank];
                            selectedNotes = ThinNotes(lower.Notes, keepRatio);
                            origin = $"adapted from {lower.SourceName} at {keepRatio:P0} density";
                        }
                        else if (upper.Notes != null)
                        {
                            double keepRatio = singleSourceKeepRatios[targetRank];
                            selectedNotes = ThinNotes(upper.Notes, keepRatio);
                            origin = $"adapted from {upper.SourceName} at {keepRatio:P0} density";
                        }
                        else
                        {
                            selectedNotes = new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                            origin = "no source notes";
                        }
                    }
                }

                string outputFile = outputFiles[targetRank];
                results[outputFile] = BuildSongJson(bpmGrid, tickToSec, selectedNotes, ticksPerBeat, outputFile, log);
                log?.Invoke($"  [Chart] {outputFile}: {CountNotes(selectedNotes)} notes; {origin}.");
            }

            return results;
        }

        private static bool HasNotes(SortedDictionary<long, List<ChartParser.NoteEvent>>? notes) =>
            notes != null && notes.Values.Any(group => group != null && group.Count > 0);

        private static int CountNotes(SortedDictionary<long, List<ChartParser.NoteEvent>>? notes) =>
            notes?.Values.Where(group => group != null).Sum(group => group.Count) ?? 0;

        private static SortedDictionary<long, List<ChartParser.NoteEvent>> CloneNotes(
            SortedDictionary<long, List<ChartParser.NoteEvent>> source)
        {
            return new SortedDictionary<long, List<ChartParser.NoteEvent>>(
                source.ToDictionary(entry => entry.Key, entry => entry.Value
                    .Select(note => new ChartParser.NoteEvent { Lane = note.Lane, DurationTicks = note.DurationTicks })
                    .ToList()));
        }

        private static List<(int TargetRank, string SourceName, SortedDictionary<long, List<ChartParser.NoteEvent>>? Notes)> GetDifficultyAnchors(
            List<(string Name, int Rank, SortedDictionary<long, List<ChartParser.NoteEvent>>? Notes)> availableSources)
        {
            int[] targetSlots = availableSources.Count switch
            {
                2 => new[] { 0, 3 },
                3 => new[] { 0, 2, 3 },
                _ => new[] { 0, 1, 2, 3 }
            };

            return availableSources.Select((source, index) =>
                (TargetRank: targetSlots[index], SourceName: source.Name, Notes: source.Notes)).ToList();
        }

        private static SortedDictionary<long, List<ChartParser.NoteEvent>> BlendNotes(
            SortedDictionary<long, List<ChartParser.NoteEvent>> easier,
            SortedDictionary<long, List<ChartParser.NoteEvent>> harder,
            double harderFraction)
        {
            var result = CloneNotes(easier);
            var additions = harder
                .Select(entry => (entry.Key, Notes: entry.Value.Where(note =>
                {
                    if (!result.TryGetValue(entry.Key, out var existing))
                        return true;
                    return !existing.Any(current => current.Lane == note.Lane && current.DurationTicks == note.DurationTicks);
                }).ToList()))
                .Where(entry => entry.Notes.Count > 0)
                .OrderBy(entry => entry.Key)
                .ToList();

            int countToAdd = Math.Clamp((int)Math.Round(additions.Count * harderFraction), 0, additions.Count);
            foreach (int index in GetEvenlySpacedIndices(additions.Count, countToAdd))
            {
                var addition = additions[index];
                if (!result.TryGetValue(addition.Key, out var group))
                {
                    group = new List<ChartParser.NoteEvent>();
                    result[addition.Key] = group;
                }
                group.AddRange(addition.Notes.Select(note => new ChartParser.NoteEvent
                {
                    Lane = note.Lane,
                    DurationTicks = note.DurationTicks
                }));
            }
            return result;
        }

        private static SortedDictionary<long, List<ChartParser.NoteEvent>> ThinNotes(
            SortedDictionary<long, List<ChartParser.NoteEvent>> source,
            double keepRatio)
        {
            var groups = source.Where(entry => entry.Value != null && entry.Value.Count > 0).ToList();
            if (groups.Count < 2 || keepRatio >= 1.0)
                return CloneNotes(source);

            int keepCount = Math.Clamp((int)Math.Round(groups.Count * keepRatio), 1, groups.Count - 1);
            var result = new SortedDictionary<long, List<ChartParser.NoteEvent>>();
            foreach (int index in GetEvenlySpacedIndices(groups.Count, keepCount))
            {
                var group = groups[index];
                result[group.Key] = group.Value
                    .Select(note => new ChartParser.NoteEvent { Lane = note.Lane, DurationTicks = note.DurationTicks })
                    .ToList();
            }
            return result;
        }

        private static IEnumerable<int> GetEvenlySpacedIndices(int totalCount, int selectedCount)
        {
            if (selectedCount <= 0 || totalCount <= 0)
                yield break;

            for (int i = 0; i < selectedCount; i++)
            {
                int index = Math.Min(totalCount - 1, (int)Math.Floor(((i + 0.5) * totalCount) / selectedCount));
                yield return index;
            }
        }
    }
}
