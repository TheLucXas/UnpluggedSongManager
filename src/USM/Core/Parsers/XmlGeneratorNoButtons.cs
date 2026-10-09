using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace USM.Core.Parsers
{
    public class XmlGeneratorNoButtons
    {
        private static readonly XNamespace NsDefault = "http://schemas.datacontract.org/2004/07/";
        private static readonly XNamespace NsI = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace NsA = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

        private static readonly Dictionary<int, int> FingerMapTier = new()
        {
            [0] = 0, [1] = 0, [2] = 1, [3] = 2, [4] = 3, [7] = 0
        };

        private static readonly Dictionary<string, int> ChordMapTier = new()
        {
            ["0,1"] = 0, ["0,2"] = 0, ["1,2"] = 1, ["1,3"] = 1,
            ["2,3"] = 2, ["2,4"] = 2, ["3,4"] = 3,
            ["0,1,2"] = 0, ["1,2,3"] = 1, ["2,3,4"] = 2
        };

        public static string BuildSongXml(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            SortedDictionary<long, List<ChartParser.NoteEvent>> notesByTick,
            int ticksPerBeat,
            string diffName)
        {
            var root = new XElement(NsDefault + "SongLevel",
                new XAttribute(XNamespace.Xmlns + "i", NsI.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", NsA.NamespaceName));

            int initialBpm = bpmGrid.Count > 0 ? (int)Math.Round(bpmGrid[0].Bpm) : 120;
            root.Add(new XElement(NsDefault + "BPM", initialBpm.ToString()));

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

            var handKeyTime = new XElement(NsDefault + "KeyTime",
                new XAttribute(NsI + "type", "GuitarGame"),
                new XElement(NsDefault + "Duration", totalDur.ToString("F6", CultureInfo.InvariantCulture)),
                new XElement(NsDefault + "Time", "0"),
                new XElement(NsDefault + "PrewarmTime", "4")
            );
            var notesHandElem = new XElement(NsDefault + "Notes");
            handKeyTime.Add(notesHandElem);

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

                var lanes = entries.Select(e => e.Lane).Distinct().OrderBy(l => l).ToList();
                long maxDurTicks = entries.Max(e => e.DurationTicks);

                double durSec = 0.0;
                if (maxDurTicks > (ticksPerBeat / 2)) durSec = Math.Max(0.0, tickToSec(tick + maxDurTicks) - noteTime);
                if (lanes.Count == 0) continue;

                int tier;
                if (lanes.Count == 1) tier = FingerMapTier.TryGetValue(lanes[0], out var t) ? t : 0;
                else
                {
                    string chordKey = string.Join(",", lanes);
                    tier = ChordMapTier.TryGetValue(chordKey, out var c) ? c : Math.Min(3, lanes.Last());
                }
                
                // Unplugged requires Easy/SuperEasy to only use 2 tiers (0 and 1). Normal/Hard use all 4.
                if (diffName == "songdata-SuperEasy.xml" || diffName == "songdata-Easy.xml")
                {
                    tier = tier % 2; // Clamp to 0 and 1
                }

                bool isPullOff = (noteTime - prevTime < 0.22 && prevTier != tier);

                // NO FINGERS NODE!
                var tierNote = new XElement(NsDefault + "TierNote");
                if (isPullOff) tierNote.Add(new XAttribute(NsI + "type", "PullOffGuitarNote"));
                tierNote.Add(new XElement(NsDefault + "Duration", durSec.ToString("F6", CultureInfo.InvariantCulture)));
                tierNote.Add(new XElement(NsDefault + "Time", noteTime.ToString("F6", CultureInfo.InvariantCulture)));
                tierNote.Add(new XElement(NsDefault + "Tier", tier.ToString()));
                tierNote.Add(new XElement(NsDefault + "CursableProbability", "0"));
                tierNote.Add(new XElement(NsDefault + "IsBlind", "false"));
                notesHandElem.Add(tierNote);

                int rotVal = (lanes.Contains(4) && !lanes.Any(l => l is 0 or 1)) ? 1 : 0;

                // NO BUTTONS NODE!
                var ctrlNote = new XElement(NsDefault + "ControllerGuitarNote",
                    new XElement(NsDefault + "Duration", durSec.ToString("F6", CultureInfo.InvariantCulture)),
                    new XElement(NsDefault + "Time", noteTime.ToString("F6", CultureInfo.InvariantCulture)),
                    new XElement(NsDefault + "Tier", tier.ToString()),
                    new XElement(NsDefault + "CursableProbability", "0"),
                    new XElement(NsDefault + "IsBlind", "false"),
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

        public static Dictionary<string, string> GenerateAllDifficulties(
            List<ChartParser.BpmEvent> bpmGrid,
            Func<long, double> tickToSec,
            Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> difficulties,
            int ticksPerBeat)
        {
            var diffMappings = new Dictionary<string, string[]>
            {
                ["songdata-Hard.xml"] = new[] { "ExpertSingle", "HardSingle", "MediumSingle", "EasySingle" },
                ["songdata-Normal.xml"] = new[] { "HardSingle", "ExpertSingle", "MediumSingle", "EasySingle" },
                ["songdata-Easy.xml"] = new[] { "MediumSingle", "EasySingle", "HardSingle", "ExpertSingle" },
                ["songdata-SuperEasy.xml"] = new[] { "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" }
            };
            var results = new Dictionary<string, string>();

            foreach (var kvp in diffMappings)
            {
                SortedDictionary<long, List<ChartParser.NoteEvent>> chosenNotes = null;
                foreach (var diffName in kvp.Value)
                {
                    if (difficulties.TryGetValue(diffName, out var notes)) { chosenNotes = notes; break; }
                }
                chosenNotes ??= new SortedDictionary<long, List<ChartParser.NoteEvent>>();
                results[kvp.Key] = BuildSongXml(bpmGrid, tickToSec, chosenNotes, ticksPerBeat, kvp.Key);
            }
            return results;
        }
    }
}
