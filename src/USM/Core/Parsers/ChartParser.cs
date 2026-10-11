using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace USM.Core.Parsers
{
    public class ChartParser
    {
        public class BpmEvent
        {
            public long Tick { get; set; }
            public double TimeSec { get; set; }
            public double Bpm { get; set; }
        }

        public class NoteEvent
        {
            public int Lane { get; set; }
            public long DurationTicks { get; set; }
            public bool FlipHopo { get; set; }
            public bool Tap { get; set; }
            public bool ForceHopo { get; set; }
            public bool ForceStrum { get; set; }
        }

        public class ParsedChart
        {
            public int Resolution { get; set; } = 192;
            public int HopoThresholdTicks => Math.Max(1, (int)Math.Floor(65.0 * Resolution / 192.0));
            public int IgnoredNonPlayableNoteEvents { get; set; }
            public List<BpmEvent> BpmGrid { get; set; } = new();
            public Func<long, double> TickToSec { get; set; } = _ => 0.0;
            public Dictionary<string, SortedDictionary<long, List<NoteEvent>>> Difficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        // Clone Hero guitar N events 5 and 6 modify playable notes at the same
        // tick; they are retained as modifiers, never emitted as extra notes.
        // Lane 7 is a valid open-note lane.
        public static bool IsPlayableGuitarLane(int lane) => (lane >= 0 && lane <= 4) || lane == 7;

        public static ParsedChart Parse(string chartPath)
        {
            if (!File.Exists(chartPath))
                throw new FileNotFoundException($"Chart file not found: {chartPath}");

            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string? currentSection = null;

            foreach (var rawLine in File.ReadLines(chartPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2);
                    if (!sections.ContainsKey(currentSection))
                        sections[currentSection] = new List<string>();
                }
                else if (currentSection != null && line != "{" && line != "}" && !string.IsNullOrEmpty(line))
                {
                    sections[currentSection].Add(line);
                }
            }

            var result = new ParsedChart();

            // 1. Resolution in [Song]
            if (sections.TryGetValue("Song", out var songLines))
            {
                foreach (var line in songLines)
                {
                    var match = Regex.Match(line, @"Resolution\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int res))
                    {
                        result.Resolution = res > 0 ? res : 192;
                        break;
                    }
                }
            }

            // 2. SyncTrack (BPMs)
            var rawBpmEvents = new List<(long Tick, double Bpm)>();
            if (sections.TryGetValue("SyncTrack", out var syncLines))
            {
                foreach (var line in syncLines)
                {
                    var match = Regex.Match(line, @"(\d+)\s*=\s*B\s+(\d+)");
                    if (match.Success)
                    {
                        long tick = long.Parse(match.Groups[1].Value);
                        double bpm = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 1000.0;
                        rawBpmEvents.Add((tick, bpm));
                    }
                }
            }

            rawBpmEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            if (rawBpmEvents.Count == 0 || rawBpmEvents[0].Tick != 0)
            {
                rawBpmEvents.Insert(0, (0, 120.0));
            }

            double curTime = 0.0;
            long lastTick = 0;
            double lastBpm = rawBpmEvents[0].Bpm;

            foreach (var (tick, bpm) in rawBpmEvents)
            {
                long deltaTicks = tick - lastTick;
                curTime += (deltaTicks / (double)result.Resolution) * (60.0 / lastBpm);
                result.BpmGrid.Add(new BpmEvent
                {
                    Tick = tick,
                    TimeSec = curTime,
                    Bpm = bpm
                });
                lastTick = tick;
                lastBpm = bpm;
            }

            result.TickToSec = tick =>
            {
                for (int i = result.BpmGrid.Count - 1; i >= 0; i--)
                {
                    var b = result.BpmGrid[i];
                    if (tick >= b.Tick)
                    {
                        return b.TimeSec + ((tick - b.Tick) * (60.0 / (b.Bpm * result.Resolution)));
                    }
                }
                return 0.0;
            };

            // 3. Chart difficulties. Chorus charts normally expose Easy through
            // Expert; Unplugged's own catalog also includes SuperEasySingle.
            string[] sectionNames = { "SuperEasySingle", "EasySingle", "MediumSingle", "HardSingle", "ExpertSingle" };
            foreach (var secName in sectionNames)
            {
                if (sections.TryGetValue(secName, out var lines))
                {
                    var noteDict = new SortedDictionary<long, List<NoteEvent>>();
                    var modifiers = new Dictionary<long, (bool Flip, bool Tap)>();
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"(\d+)\s*=\s*N\s+(\d+)\s+(\d+)");
                        if (match.Success)
                        {
                            long tick = long.Parse(match.Groups[1].Value);
                            int lane = int.Parse(match.Groups[2].Value);
                            long dur = long.Parse(match.Groups[3].Value);

                            if (lane is 5 or 6)
                            {
                                modifiers.TryGetValue(tick, out var state);
                                modifiers[tick] = (state.Flip || lane == 5, state.Tap || lane == 6);
                                continue;
                            }

                            // Do not create gameplay notes from HOPO/tap markers or
                            // unknown lanes. A marker on its own used to become a
                            // ControllerNote with Buttons=[false,false].
                            if (!IsPlayableGuitarLane(lane))
                            {
                                result.IgnoredNonPlayableNoteEvents++;
                                continue;
                            }

                            if (!noteDict.ContainsKey(tick))
                                noteDict[tick] = new List<NoteEvent>();

                            noteDict[tick].Add(new NoteEvent { Lane = lane, DurationTicks = dur });
                        }
                    }
                    foreach (var entry in noteDict)
                    {
                        if (!modifiers.TryGetValue(entry.Key, out var state)) continue;
                        foreach (var note in entry.Value)
                        {
                            note.FlipHopo = state.Flip;
                            note.Tap = state.Tap;
                        }
                    }
                    if (noteDict.Count > 0)
                    {
                        result.Difficulties[secName] = noteDict;
                    }
                }
            }

            return result;
        }
    }
}
