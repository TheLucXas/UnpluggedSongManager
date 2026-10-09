using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace USM.Core.Parsers
{
    public class JsonGenerator
    {
        private static readonly Dictionary<int, (int Tier, string[] Fingers)> FingerMap = new()
        {
            [0] = (0, new[] { "Index" }),
            [1] = (0, new[] { "Index", "Middle" }),
            [2] = (1, new[] { "Index", "Middle", "Ring" }),
            [3] = (2, new[] { "Index", "Middle", "Ring", "Pinky" }),
            [4] = (3, new[] { "Index", "Middle", "Ring", "Pinky" }),
            [7] = (0, new[] { "Index" }) // Open note
        };

        private static readonly Dictionary<string, (int Tier, string[] Fingers)> ChordMap = new()
        {
            ["0,1"] = (0, new[] { "Index", "Middle" }),
            ["0,2"] = (0, new[] { "Index", "Ring" }), // Power Chord
            ["1,2"] = (1, new[] { "Index", "Middle" }),
            ["1,3"] = (1, new[] { "Index", "Ring" }), // Power Chord
            ["2,3"] = (2, new[] { "Index", "Middle" }),
            ["2,4"] = (2, new[] { "Index", "Ring" }), // Power Chord
            ["3,4"] = (3, new[] { "Index", "Middle" }),
            ["0,1,2"] = (0, new[] { "Index", "Middle", "Ring" }),
            ["1,2,3"] = (1, new[] { "Index", "Middle", "Ring" }),
            ["2,3,4"] = (2, new[] { "Index", "Middle", "Ring" })
        };

        private static string Escape(string s) => s.Replace("\"", "\\\"");
        
        public static string BuildSongJson(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            int ticksPerBeat)
        {
            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            long lastTick = notesByTick.Count > 0 ? notesByTick.Keys.Last() : 0;
            double totalDur = tickToSec(lastTick) + 10.0;

            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"BPM\":{initialBpm},");
            
            sb.Append("\"BPMGrid\":[");
            for (int i = 0; i < bpmGrid.Count; i++)
            {
                var b = bpmGrid[i];
                sb.Append($"\"{b.TimeSec.ToString("F6", CultureInfo.InvariantCulture)}_{b.Bpm.ToString("F3", CultureInfo.InvariantCulture)}\"");
                if (i < bpmGrid.Count - 1) sb.Append(",");
            }
            sb.Append("],");

            sb.Append($"\"Duration\":{totalDur.ToString("F6", CultureInfo.InvariantCulture)},");

            // We will output notes in two arrays: HandNotes and CtrlNotes to save space
            sb.Append("\"HandNotes\":[");
            bool firstHand = true;
            bool firstCtrl = true;

            StringBuilder ctrlSb = new StringBuilder();
            ctrlSb.Append("\"CtrlNotes\":[");

            double prevTime = -999.0;
            int prevTier = -1;

            foreach (var kvp in notesByTick)
            {
                long tick = kvp.Key;
                var entries = kvp.Value;
                double noteTime = tickToSec(tick);

                var lanes = entries.Select(e => e.Lane).Distinct().OrderBy(l => l).ToList();
                long maxDurTicks = entries.Max(e => e.DurationTicks);

                double durSec = 0.0;
                if (maxDurTicks > (ticksPerBeat / 2))
                {
                    durSec = Math.Max(0.0, tickToSec(tick + maxDurTicks) - noteTime);
                }

                if (lanes.Count == 0) continue;

                int tier;
                string[] fingers;

                if (lanes.Count == 1)
                {
                    int lane = lanes[0];
                    if (FingerMap.TryGetValue(lane, out var f))
                        (tier, fingers) = f;
                    else
                        (tier, fingers) = (0, new[] { "Index" });
                }
                else
                {
                    string chordKey = string.Join(",", lanes);
                    if (ChordMap.TryGetValue(chordKey, out var c))
                        (tier, fingers) = c;
                    else
                    {
                        tier = Math.Min(3, lanes.Last());
                        fingers = new[] { "Index", "Middle", "Ring" };
                    }
                }

                bool isPullOff = (noteTime - prevTime < 0.22 && prevTier != tier);

                // Hand String Format: "time_dur_tier_isPullOff_finger1,finger2"
                string fStr = string.Join(",", fingers);
                string poStr = isPullOff ? "1" : "0";
                
                if (!firstHand) sb.Append(",");
                sb.Append($"\"{noteTime.ToString("F3", CultureInfo.InvariantCulture)}_{durSec.ToString("F3", CultureInfo.InvariantCulture)}_{tier}_{poStr}_{fStr}\"");
                firstHand = false;

                // Controller Note Buttons & Rotation
                int btn1 = 0, btn2 = 0, rotVal = 0;

                if (lanes.Count == 1)
                {
                    int lane = lanes[0];
                    if (lane is 0 or 1) btn1 = 1;
                    else if (lane is 2 or 3) btn2 = 1;
                    else if (lane == 4) { btn2 = 1; rotVal = 1; }
                    else btn1 = 1;
                }
                else
                {
                    bool hasLower = lanes.Any(l => l is 0 or 1);
                    bool hasUpper = lanes.Any(l => l is 2 or 3 or 4);
                    rotVal = (lanes.Contains(4) && !hasLower) ? 1 : 0;
                    if (hasLower && hasUpper) { btn1 = 1; btn2 = 1; }
                    else if (hasLower) { btn1 = 1; }
                    else { btn2 = 1; }
                }

                // Ctrl String Format: "time_dur_tier_btn1_btn2_rot"
                if (!firstCtrl) ctrlSb.Append(",");
                ctrlSb.Append($"\"{noteTime.ToString("F3", CultureInfo.InvariantCulture)}_{durSec.ToString("F3", CultureInfo.InvariantCulture)}_{tier}_{btn1}_{btn2}_{rotVal}\"");
                firstCtrl = false;

                prevTime = noteTime;
                prevTier = tier;
            }

            sb.Append("],");
            ctrlSb.Append("]");
            
            sb.Append(ctrlSb.ToString());
            sb.Append("}");

            return sb.ToString();
        }

        public static Dictionary<string, string> GenerateAllDifficultiesJson(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> difficulties,
            int ticksPerBeat)
        {
            var diffMappings = new Dictionary<string, string[]>
            {
                ["songdata-Hard.json"] = new[] { "ExpertSingle", "HardSingle", "MediumSingle", "EasySingle" },
                ["songdata-Normal.json"] = new[] { "HardSingle", "ExpertSingle", "MediumSingle", "EasySingle" },
                ["songdata-Easy.json"] = new[] { "MediumSingle", "EasySingle", "HardSingle", "ExpertSingle" },
                ["songdata-SuperEasy.json"] = new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" }
            };

            var results = new Dictionary<string, string>();

            foreach (var kvp in diffMappings)
            {
                SortedDictionary<long, List<ChartParser.NoteEvent>>? chosenNotes = null;
                foreach (var diffName in kvp.Value)
                {
                    if (difficulties.TryGetValue(diffName, out var notes))
                    {
                        chosenNotes = notes;
                        break;
                    }
                }

                chosenNotes ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                results[kvp.Key] = BuildSongJson(bpmGrid, tickToSec, chosenNotes, ticksPerBeat);
            }

            return results;
        }
    }
}
