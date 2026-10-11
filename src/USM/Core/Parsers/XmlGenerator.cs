using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace USM.Core.Parsers
{
    public class XmlGenerator
    {
        private static readonly XNamespace NsDefault = "http://schemas.datacontract.org/2004/07/";
        private static readonly XNamespace NsI = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace NsA = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

        // Quest's IL2CPP runtime cannot safely populate List<TierFinger> through
        // the mod's generic bridge. Build the native hand mapping ahead of time
        // from the canonical JSON, so gameplay only deserializes game data.
        public static string BuildQuestHandXml(string chartJson)
        {
            using JsonDocument document = JsonDocument.Parse(chartJson);
            JsonElement chart = document.RootElement;
            var root = new XElement(NsDefault + "SongLevel",
                new XAttribute(XNamespace.Xmlns + "i", NsI.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", NsA.NamespaceName),
                new XElement(NsDefault + "BPM", chart.GetProperty("BPM").GetRawText()));

            var bpmGrid = new XElement(NsDefault + "BPMGrid");
            foreach (JsonElement beat in chart.GetProperty("BPMGrid").EnumerateArray())
            {
                bpmGrid.Add(new XElement(NsDefault + "BPMKey",
                    new XElement(NsDefault + "Duration", "0"),
                    new XElement(NsDefault + "Time", beat.GetProperty("Time").GetRawText()),
                    new XElement(NsDefault + "BPM", beat.GetProperty("BPM").GetRawText())));
            }
            root.Add(bpmGrid,
                new XElement(NsDefault + "BeatGrid", new XAttribute(NsI + "nil", "true")),
                new XElement(NsDefault + "BeatGridFromFile", "false"));

            var notes = new XElement(NsDefault + "Notes");
            foreach (JsonElement note in chart.GetProperty("HandNotes").EnumerateArray())
            {
                var tierNote = new XElement(NsDefault + "TierNote");
                if (note.GetProperty("IsPullOff").GetBoolean())
                    tierNote.Add(new XAttribute(NsI + "type", "PullOffGuitarNote"));
                tierNote.Add(new XElement(NsDefault + "Duration", note.GetProperty("Duration").GetRawText()),
                    new XElement(NsDefault + "Time", note.GetProperty("Time").GetRawText()),
                    new XElement(NsDefault + "Tier", note.GetProperty("StringLane").GetRawText()),
                    new XElement(NsDefault + "CursableProbability", "0"),
                    new XElement(NsDefault + "IsBlind", "false"));
                var fingers = new XElement(NsDefault + "Fingers");
                foreach (JsonElement finger in note.GetProperty("Fingers").EnumerateArray())
                    fingers.Add(new XElement(NsDefault + "TierFinger", finger.GetString()));
                tierNote.Add(fingers);
                notes.Add(tierNote);
            }

            root.Add(new XElement(NsDefault + "Keys",
                new XElement(NsDefault + "KeyTime",
                    new XAttribute(NsI + "type", "GuitarGame"),
                    new XElement(NsDefault + "Duration", chart.GetProperty("Duration").GetRawText()),
                    new XElement(NsDefault + "Time", "0"),
                    new XElement(NsDefault + "PrewarmTime", "4"),
                    notes)));
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
        }

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

        public static string BuildSongXml(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            int ticksPerBeat)
        {
            var root = new XElement(NsDefault + "SongLevel",
                new XAttribute(XNamespace.Xmlns + "i", NsI.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", NsA.NamespaceName));

            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            root.Add(new XElement(NsDefault + "BPM", initialBpm.ToString()));

            // BPMGrid
            var bpmGridElem = new XElement(NsDefault + "BPMGrid");
            foreach (var b in bpmGrid)
            {
                var keyElem = new XElement(NsDefault + "BPMKey",
                    new XElement(NsDefault + "Duration", "0"),
                    new XElement(NsDefault + "Time", b.TimeSec.ToString("F6", CultureInfo.InvariantCulture)),
                    new XElement(NsDefault + "BPM", b.Bpm.ToString("F3", CultureInfo.InvariantCulture))
                );
                bpmGridElem.Add(keyElem);
            }
            root.Add(bpmGridElem);

            root.Add(new XElement(NsDefault + "BeatGrid", new XAttribute(NsI + "nil", "true")));
            root.Add(new XElement(NsDefault + "BeatGridFromFile", "false"));
            root.Add(new XElement(NsDefault + "EditorBPM", "0"));

            var keysElem = new XElement(NsDefault + "Keys");

            long lastTick = notesByTick.Count > 0 ? notesByTick.Keys.Last() : 0;
            double totalDur = tickToSec(lastTick) + 10.0;

            // 1. Hand Tracking Track
            var handKeyTime = new XElement(NsDefault + "KeyTime",
                new XAttribute(NsI + "type", "GuitarGame"),
                new XElement(NsDefault + "Duration", totalDur.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement(NsDefault + "Time", "0"),
                new XElement(NsDefault + "PrewarmTime", "4")
            );
            var notesHandElem = new XElement(NsDefault + "Notes");
            handKeyTime.Add(notesHandElem);

            // 2. Controller Track
            var ctrlKeyTime = new XElement(NsDefault + "KeyTime",
                new XAttribute(NsI + "type", "GuitarControllerGame"),
                new XElement(NsDefault + "Duration", totalDur.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement(NsDefault + "Time", "0"),
                new XElement(NsDefault + "PrewarmTime", "4")
            );
            var notesCtrlElem = new XElement(NsDefault + "Notes");
            ctrlKeyTime.Add(notesCtrlElem);

            double prevTime = -999.0;
            int prevTier = -1;

            foreach (var kvp in notesByTick)
            {
                long tick = kvp.Key;
                var entries = kvp.Value;
                double noteTime = tickToSec(tick);

                var lanes = entries
                    .Select(e => e.Lane)
                    .Where(ChartParser.IsPlayableGuitarLane)
                    .Distinct()
                    .OrderBy(l => l)
                    .ToList();
                if (lanes.Count == 0) continue;
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
                    {
                        (tier, fingers) = c;
                    }
                    else
                    {
                        tier = Math.Min(3, lanes.Last());
                        fingers = new[] { "Index", "Middle", "Ring" };
                    }
                }

                bool isPullOff = (noteTime - prevTime < 0.22 && prevTier != tier);

                // Hand Tracking Note
                var tierNote = new XElement(NsDefault + "TierNote");
                if (isPullOff)
                {
                    tierNote.Add(new XAttribute(NsI + "type", "PullOffGuitarNote"));
                }
                tierNote.Add(new XElement(NsDefault + "Duration", durSec.ToString("F6", CultureInfo.InvariantCulture)));
                tierNote.Add(new XElement(NsDefault + "Time", noteTime.ToString("F6", CultureInfo.InvariantCulture)));
                tierNote.Add(new XElement(NsDefault + "Tier", tier.ToString()));
                tierNote.Add(new XElement(NsDefault + "CursableProbability", "0"));
                tierNote.Add(new XElement(NsDefault + "IsBlind", "false"));

                var fingersElem = new XElement(NsDefault + "Fingers");
                foreach (var finger in fingers)
                {
                    fingersElem.Add(new XElement(NsDefault + "TierFinger", finger));
                }
                tierNote.Add(fingersElem);
                notesHandElem.Add(tierNote);

                // Controller Note Buttons & Rotation
                bool btn1, btn2;
                int rotVal;

                if (lanes.Count == 1)
                {
                    int lane = lanes[0];
                    if (lane is 0 or 1)
                    {
                        btn1 = true; btn2 = false; rotVal = 0;
                    }
                    else if (lane is 2 or 3)
                    {
                        btn1 = false; btn2 = true; rotVal = 0;
                    }
                    else if (lane == 4)
                    {
                        btn1 = false; btn2 = true; rotVal = 1;
                    }
                    else
                    {
                        btn1 = true; btn2 = false; rotVal = 0;
                    }
                }
                else
                {
                    bool hasLower = lanes.Any(l => l is 0 or 1);
                    bool hasUpper = lanes.Any(l => l is 2 or 3 or 4);
                    rotVal = (lanes.Contains(4) && !hasLower) ? 1 : 0;

                    if (hasLower && hasUpper)
                    {
                        btn1 = true; btn2 = true;
                    }
                    else if (hasLower)
                    {
                        btn1 = true; btn2 = false;
                    }
                    else
                    {
                        btn1 = false; btn2 = true;
                    }
                }

                var ctrlNote = new XElement(NsDefault + "ControllerGuitarNote",
                    new XElement(NsDefault + "Duration", durSec.ToString("F6", CultureInfo.InvariantCulture)),
                    new XElement(NsDefault + "Time", noteTime.ToString("F6", CultureInfo.InvariantCulture)),
                    new XElement(NsDefault + "Tier", tier.ToString()),
                    new XElement(NsDefault + "CursableProbability", "0"),
                    new XElement(NsDefault + "IsBlind", "false"),
                    new XElement(NsDefault + "Buttons",
                        new XElement(NsA + "boolean", btn1 ? "true" : "false"),
                        new XElement(NsA + "boolean", btn2 ? "true" : "false")
                    ),
                    new XElement(NsDefault + "ChordRotation", rotVal.ToString()),
                    new XElement(NsDefault + "Intensity", "0")
                );
                notesCtrlElem.Add(ctrlNote);

                prevTime = noteTime;
                prevTier = tier;
            }

            keysElem.Add(handKeyTime);
            keysElem.Add(ctrlKeyTime);
            root.Add(keysElem);

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            return doc.Declaration?.ToString() + Environment.NewLine + doc.ToString();
        }

        public static void GenerateAllDifficulties(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> difficulties,
            int ticksPerBeat,
            string outDir,
            Action<string>? log = null)
        {
            var diffMappings = new Dictionary<string, string[]>
            {
                ["songdata-Hard.xml"] = new[] { "ExpertSingle", "HardSingle", "MediumSingle", "EasySingle" },
                ["songdata-Normal.xml"] = new[] { "HardSingle", "ExpertSingle", "MediumSingle", "EasySingle" },
                ["songdata-Easy.xml"] = new[] { "MediumSingle", "EasySingle", "HardSingle", "ExpertSingle" },
                ["songdata-SuperEasy.xml"] = new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" }
            };

            foreach (var kvp in diffMappings)
            {
                string fileName = kvp.Key;
                string[] candidates = kvp.Value;

                SortedDictionary<long, List<ChartParser.NoteEvent>>? selectedNotes = null;
                foreach (var cand in candidates)
                {
                    if (difficulties.TryGetValue(cand, out var notes) && notes.Count > 0)
                    {
                        selectedNotes = notes;
                        break;
                    }
                }

                selectedNotes ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();

                string xmlContent = BuildSongXml(bpmGrid, tickToSec, selectedNotes, ticksPerBeat);
                string outPath = Path.Combine(outDir, fileName);
                File.WriteAllText(outPath, xmlContent, Encoding.UTF8);

                log?.Invoke($"  [XML] {fileName}: {selectedNotes.Count} notes generated.");
            }
        }
    }
}
