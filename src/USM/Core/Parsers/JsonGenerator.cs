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
        public const int ParserVersion = 7;

        // These are spatial string lanes (the four white paths in the game),
        // independent of difficulty and source fret/button colors. Every output
        // difficulty can use all four paths. Weights are the aggregate note-lane
        // distribution across all 57 official PC charts and four difficulties.
        private static readonly double[] OfficialStringLaneWeights =
            { 0.14574, 0.38999, 0.36986, 0.09441 };

        // Median controller-note NPS measured from the 57 official PC song
        // archives available to the catalog scan (base game and installed DLC).
        // They are used as a ceiling when a custom chart has to supply an easier
        // difficulty. The parser never invents extra notes when a source is too
        // sparse to reach one of these targets.
        private static readonly double[] OfficialDifficultyMedianNps =
            { 2.55799, 2.59365, 3.30484, 3.57935 };

        // Vanilla SuperEasy uses at most two distinct controller input
        // combinations. This is independent of StringLane, which still uses all
        // four spatial paths in every generated difficulty.
        private const int SuperEasyControllerPatternLimit = 2;

        private static readonly Dictionary<int, string[]> FingerMap = new()
        {
            [0] = new[] { "Index" },
            [1] = new[] { "Index", "Middle" },
            [2] = new[] { "Index", "Middle", "Ring" },
            [3] = new[] { "Index", "Middle", "Ring", "Pinky" },
            [4] = new[] { "Index", "Middle", "Ring", "Pinky" },
            [7] = new[] { "Index" }
        };

        private static readonly Dictionary<string, string[]> ChordMap = new()
        {
            ["0,1"] = new[] { "Index", "Middle" },
            ["0,2"] = new[] { "Index", "Ring" },
            ["1,2"] = new[] { "Index", "Middle" },
            ["1,3"] = new[] { "Index", "Ring" },
            ["2,3"] = new[] { "Index", "Middle" },
            ["2,4"] = new[] { "Index", "Ring" },
            ["3,4"] = new[] { "Index", "Middle" },
            ["0,1,2"] = new[] { "Index", "Middle", "Ring" },
            ["1,2,3"] = new[] { "Index", "Middle", "Ring" },
            ["2,3,4"] = new[] { "Index", "Middle", "Ring" }
        };

        private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        public static string BuildSongJson(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            int ticksPerBeat,
            string outputDifficulty,
            Action<string>? log = null,
            double songDurationSeconds = 0)
        {
            bpmGrid ??= new List<ChartParser.BpmEvent>();
            notesByTick ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
            tickToSec ??= _ => 0.0;

            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            long lastTick = notesByTick.Count > 0 ? notesByTick.Keys.Last() : 0;
            double totalDuration = Math.Max(Math.Max(0, tickToSec(lastTick)) + 10.0, songDurationSeconds);
            var sb = new StringBuilder(Math.Max(1024, notesByTick.Count * 140));
            sb.Append("{\"SchemaVersion\":3,")
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
            var controllerPatternRemap = BuildControllerPatternRemap(notesByTick, outputDifficulty);
            if (controllerPatternRemap.Any(pair => pair.Key != pair.Value))
            {
                string remapDetails = string.Join(",", controllerPatternRemap
                    .Where(pair => pair.Key != pair.Value)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}->{pair.Value}"));
                log?.Invoke($"  [ChartDiag] {outputDifficulty}: SuperEasy controller input combinations simplified to at most {SuperEasyControllerPatternLimit} ({remapDetails}); spatial string lanes remain independent.");
            }
            int[] laneAssignedCounts = new int[4];
            int[] laneTargets = BuildStringLaneTargets(notesByTick);
            int generatedNoteIndex = 0;

            double previousTime = -999.0;
            string previousFingers = string.Empty;

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

                string[] fingers;
                if (lanes.Count == 1)
                {
                    if (!FingerMap.TryGetValue(lanes[0], out var fingerMapping))
                        fingerMapping = new[] { "Index" };
                    fingers = fingerMapping;
                }
                else
                {
                    string chordKey = string.Join(",", lanes);
                    if (!ChordMap.TryGetValue(chordKey, out var chordMapping))
                        chordMapping = new[] { "Index", "Middle", "Ring" };
                    fingers = chordMapping;
                }

                // Tier selects the spatial string/path in Unplugged. It must
                // not be inferred from Clone Hero's fret lane: those frets map
                // independently to Fingers and controller Buttons.
                int tier = SelectStringLane(generatedNoteIndex++, laneAssignedCounts, laneTargets);
                tierCounts[tier] = tierCounts.TryGetValue(tier, out int tierCount) ? tierCount + 1 : 1;

                string fingerShape = string.Join("+", fingers);
                bool isPullOff = noteTime - previousTime < 0.22
                    && previousFingers.Length > 0
                    && !previousFingers.Equals(fingerShape, StringComparison.Ordinal);

                if (!firstHand) sb.Append(',');
                sb.Append("{\"Time\":").Append(Number(noteTime))
                  .Append(",\"Duration\":").Append(Number(noteDuration))
                  .Append(",\"StringLane\":").Append(tier)
                  .Append(",\"Fingers\":[");
                for (int i = 0; i < fingers.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(fingers[i]).Append('"');
                }
                sb.Append("],\"IsPullOff\":").Append(isPullOff ? "true" : "false").Append('}');
                firstHand = false;

                GetControllerButtons(lanes, out bool trigger, out bool grip);
                string sourceButtonsKey = $"{(trigger ? 1 : 0)}{(grip ? 1 : 0)}";
                string buttonsKey = controllerPatternRemap.TryGetValue(sourceButtonsKey, out string? mappedButtonsKey)
                    ? mappedButtonsKey
                    : sourceButtonsKey;
                trigger = buttonsKey[0] == '1';
                grip = buttonsKey[1] == '1';
                controllerButtonCounts[buttonsKey] = controllerButtonCounts.TryGetValue(buttonsKey, out int buttonCount)
                    ? buttonCount + 1
                    : 1;
                if (!firstController) controllerNotes.Append(',');
                controllerNotes.Append("{\"Time\":").Append(Number(noteTime))
                    .Append(",\"Duration\":").Append(Number(noteDuration))
                    .Append(",\"StringLane\":").Append(tier)
                    .Append(",\"Buttons\":[").Append(trigger ? "true" : "false").Append(',')
                    .Append(grip ? "true" : "false").Append(']')
                    // Clone Hero charts encode fret lanes, not Unplugged's guitar
                    // rotation gestures. Inferring a rotation from lane 4 (orange)
                    // generated false arrows on otherwise ordinary notes.
                    .Append(",\"ChordRotation\":0")
                    .Append(",\"Intensity\":0}");
                firstController = false;

                previousTime = noteTime;
                previousFingers = fingerShape;
            }

            sb.Append(controllerNotes).Append("]}");
            log?.Invoke($"  [ChartDiag] {outputDifficulty}: stringLanes=[{string.Join(",", tierCounts.Select(pair => $"{pair.Key}:{pair.Value}"))}], fretButtons[trigger,grip]=[{string.Join(",", controllerButtonCounts.Select(pair => $"{pair.Key}:{pair.Value}"))}].");
            return sb.ToString();
        }

        private static int[] BuildStringLaneTargets(
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick)
        {
            int noteGroupCount = notesByTick.Count(entry => entry.Value != null
                && entry.Value.Any(note => ChartParser.IsPlayableGuitarLane(note.Lane)));
            var targets = new int[OfficialStringLaneWeights.Length];
            if (noteGroupCount <= 0)
                return targets;

            int minimumPerLane = noteGroupCount >= targets.Length ? 1 : 0;
            int remaining = noteGroupCount - minimumPerLane * targets.Length;
            var fractionalRemainders = new double[targets.Length];
            int assigned = minimumPerLane * targets.Length;
            for (int lane = 0; lane < targets.Length; lane++)
            {
                double exactShare = remaining * OfficialStringLaneWeights[lane];
                targets[lane] = minimumPerLane + (int)Math.Floor(exactShare);
                fractionalRemainders[lane] = exactShare - Math.Floor(exactShare);
                assigned += targets[lane];
            }

            // Keep the final counts on the official distribution while ensuring
            // a short chart still reaches every spatial lane whenever it has at
            // least four note groups. Without this floor, rare outer lanes could
            // disappear from an entire difficulty.
            while (assigned < noteGroupCount)
            {
                int lane = Enumerable.Range(0, targets.Length)
                    .OrderByDescending(candidate => fractionalRemainders[candidate])
                    .First();
                targets[lane]++;
                fractionalRemainders[lane] = -1.0;
                assigned++;
            }
            return targets;
        }

        private static int SelectStringLane(int noteIndex, int[] assignedCounts, int[] targetCounts)
        {
            // Largest-deficit scheduling spreads each lane through the song and
            // reaches the precomputed totals exactly by the final note group.
            double bestDeficit = double.NegativeInfinity;
            int selected = 0;
            int tieOffset = noteIndex % assignedCounts.Length;
            for (int offset = 0; offset < assignedCounts.Length; offset++)
            {
                int lane = (offset + tieOffset) % assignedCounts.Length;
                double expected = (noteIndex + 1) * targetCounts[lane] / Math.Max(1.0, targetCounts.Sum());
                double deficit = expected - assignedCounts[lane];
                if (deficit > bestDeficit)
                {
                    bestDeficit = deficit;
                    selected = lane;
                }
            }
            assignedCounts[selected]++;
            return selected;
        }

        private static Dictionary<string, string> BuildControllerPatternRemap(
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            string outputDifficulty)
        {
            int patternLimit = GetDifficultyRank(outputDifficulty) == 0
                ? SuperEasyControllerPatternLimit
                : int.MaxValue;
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in notesByTick)
            {
                var lanes = entry.Value
                    .Select(note => note.Lane)
                    .Where(ChartParser.IsPlayableGuitarLane)
                    .Distinct()
                    .ToList();
                if (lanes.Count == 0)
                    continue;
                GetControllerButtons(lanes, out bool trigger, out bool grip);
                string pattern = $"{(trigger ? 1 : 0)}{(grip ? 1 : 0)}";
                frequencies[pattern] = frequencies.TryGetValue(pattern, out int count) ? count + 1 : 1;
            }

            if (frequencies.Count <= patternLimit)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var retained = frequencies
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(patternLimit)
                .ToList();
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in frequencies.Keys)
            {
                if (retained.Any(pattern => pattern.Key == source))
                {
                    remap[source] = source;
                    continue;
                }

                // A SuperEasy chart may use no more than two input combinations.
                // Map excluded patterns to the closest common retained pattern;
                // this does not change the four spatial string lanes.
                string target = retained
                    .OrderBy(pattern => ButtonPatternDistance(source, pattern.Key))
                    .ThenByDescending(pattern => pattern.Value)
                    .ThenBy(pattern => pattern.Key, StringComparer.Ordinal)
                    .First().Key;
                remap[source] = target;
            }
            return remap;
        }

        private static int ButtonPatternDistance(string first, string second) =>
            (first[0] == second[0] ? 0 : 1) + (first[1] == second[1] ? 0 : 1);

        private static int GetDifficultyRank(string difficultyName)
        {
            if (difficultyName.IndexOf("SuperEasy", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (difficultyName.IndexOf("Easy", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (difficultyName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            return 3;
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
            Action<string>? log = null,
            double songDurationSeconds = 0)
        {
            var outputFiles = new[]
            {
                "songdata-SuperEasy.json",
                "songdata-Easy.json",
                "songdata-Normal.json",
                "songdata-Hard.json"
            };
            difficulties ??= new Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>>(StringComparer.OrdinalIgnoreCase);
            bool hasNativeSuperEasy = difficulties.TryGetValue("SuperEasySingle", out var nativeSuperEasyNotes)
                && HasNotes(nativeSuperEasyNotes);
            string[] sourceDifficulties = hasNativeSuperEasy
                ? new[] { "SuperEasySingle", "EasySingle", "MediumSingle", "HardSingle" }
                : new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" };
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
                    double keepRatio = GetVanillaCalibratedKeepRatio(
                        availableSources[0].Notes!, tickToSec, targetRank, songDurationSeconds);
                    selectedNotes = ThinNotes(availableSources[0].Notes!, keepRatio);
                    origin = $"adapted from {availableSources[0].Name} at {keepRatio:P0} density (vanilla NPS target {OfficialDifficultyMedianNps[targetRank]:F2})";
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
                            double lowerNps = OfficialDifficultyMedianNps[lower.TargetRank];
                            double upperNps = OfficialDifficultyMedianNps[upper.TargetRank];
                            double fraction = upperNps > lowerNps
                                ? (OfficialDifficultyMedianNps[targetRank] - lowerNps) / (upperNps - lowerNps)
                                : (targetRank - lower.TargetRank) / (double)(upper.TargetRank - lower.TargetRank);
                            fraction = Math.Clamp(fraction, 0.0, 1.0);
                            selectedNotes = BlendNotes(lower.Notes, upper.Notes, fraction);
                            origin = $"interpolated between {lower.SourceName} and {upper.SourceName} by vanilla NPS profile ({fraction:P0})";
                        }
                        else if (lower.Notes != null)
                        {
                            // A lower source tier cannot tell us which extra
                            // notes belong in a harder tier. Preserve its notes
                            // rather than inventing timing or fret events.
                            selectedNotes = CloneNotes(lower.Notes);
                            origin = $"kept {lower.SourceName}; no harder source tier was available to infer extra notes";
                        }
                        else if (upper.Notes != null)
                        {
                            double keepRatio = GetVanillaCalibratedKeepRatio(
                                upper.Notes, tickToSec, targetRank, songDurationSeconds);
                            selectedNotes = ThinNotes(upper.Notes, keepRatio);
                            origin = $"adapted from {upper.SourceName} at {keepRatio:P0} density (vanilla NPS target {OfficialDifficultyMedianNps[targetRank]:F2})";
                        }
                        else
                        {
                            selectedNotes = new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                            origin = "no source notes";
                        }
                    }
                }

                string outputFile = outputFiles[targetRank];
                results[outputFile] = BuildSongJson(bpmGrid, tickToSec, selectedNotes, ticksPerBeat, outputFile, log, songDurationSeconds);
                log?.Invoke($"  [Chart] {outputFile}: {CountNotes(selectedNotes)} notes; {origin}.");
            }

            return results;
        }

        private static double GetVanillaCalibratedKeepRatio(
            SortedDictionary<long, List<ChartParser.NoteEvent>> source,
            Func<long, double> tickToSec,
            int targetRank,
            double songDurationSeconds)
        {
            if (!HasNotes(source))
                return 1.0;

            double firstNoteTime = tickToSec(source.First(entry => entry.Value != null && entry.Value.Count > 0).Key);
            double lastNoteTime = tickToSec(source.Last(entry => entry.Value != null && entry.Value.Count > 0).Key);
            double activeChartDuration = Math.Max(0.0, lastNoteTime - firstNoteTime);
            double measuredDuration = songDurationSeconds > 1.0
                ? songDurationSeconds
                : Math.Max(1.0, activeChartDuration);
            double sourceNps = CountNotes(source) / measuredDuration;
            if (sourceNps <= 0.0)
                return 1.0;

            // A target NPS is a ceiling: if the only source chart is already
            // easier than the vanilla profile, keep it intact rather than
            // generating fake notes to make a higher tier appear harder.
            return Math.Clamp(OfficialDifficultyMedianNps[targetRank] / sourceNps, 0.0, 1.0);
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
            return availableSources
                .Select(source => (TargetRank: source.Rank, SourceName: source.Name, Notes: source.Notes))
                .ToList();
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
