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
        public const int ParserVersion = 4;

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
            string outputDifficulty)
        {
            bpmGrid ??= new List<ChartParser.BpmEvent>();
            notesByTick ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
            tickToSec ??= _ => 0.0;

            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            long lastTick = notesByTick.Count > 0 ? notesByTick.Keys.Last() : 0;
            double totalDuration = Math.Max(0, tickToSec(lastTick)) + 10.0;
            bool twoTierDifficulty = outputDifficulty.Equals("songdata-Easy.json", StringComparison.OrdinalIgnoreCase)
                || outputDifficulty.Equals("songdata-SuperEasy.json", StringComparison.OrdinalIgnoreCase);

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

                // Easy and SuperEasy use only tiers 0 and 1 in both play modes.
                // Clamp higher source tiers into tier 1; modulo wrapped tier 2
                // back to tier 0, which made the visible fret disagree with the
                // trigger/grip state generated from the source lane.
                if (twoTierDifficulty)
                {
                    tier = Math.Min(tier, 1);
                    fingers = fingers
                        .Where(finger => finger == "Index" || finger == "Middle")
                        .ToArray();
                    if (fingers.Length == 0)
                        fingers = new[] { tier == 0 ? "Index" : "Middle" };
                }

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
            int ticksPerBeat)
        {
            var difficultyMappings = new Dictionary<string, string[]>
            {
                ["songdata-Hard.json"] = new[] { "ExpertSingle", "HardSingle", "MediumSingle", "EasySingle" },
                ["songdata-Normal.json"] = new[] { "HardSingle", "ExpertSingle", "MediumSingle", "EasySingle" },
                ["songdata-Easy.json"] = new[] { "MediumSingle", "EasySingle", "HardSingle", "ExpertSingle" },
                ["songdata-SuperEasy.json"] = new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" }
            };

            var results = new Dictionary<string, string>();
            foreach (var difficulty in difficultyMappings)
            {
                SortedDictionary<long, List<ChartParser.NoteEvent>>? selectedNotes = null;
                foreach (string sourceDifficulty in difficulty.Value)
                {
                    if (difficulties.TryGetValue(sourceDifficulty, out var notes) && notes.Count > 0)
                    {
                        selectedNotes = notes;
                        break;
                    }
                }

                selectedNotes ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                results[difficulty.Key] = BuildSongJson(bpmGrid, tickToSec, selectedNotes, ticksPerBeat, difficulty.Key);
            }

            return results;
        }
    }
}
