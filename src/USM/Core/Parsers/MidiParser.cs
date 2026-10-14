using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace USM.Core.Parsers
{
    public class MidiParser
    {
        public class MidiNote
        {
            public int NoteNumber { get; set; }
            public long StartTick { get; set; }
            public long DurationTicks { get; set; }
        }

        public class MidiTrack
        {
            public string Name { get; set; } = string.Empty;
            public List<MidiNote> Notes { get; set; } = new();
        }

        public class ParsedMidi
        {
            public int TicksPerBeat { get; set; } = 480;
            public int HopoThresholdTicks => Math.Max(1, TicksPerBeat / 3 + 1);
            public List<ChartParser.BpmEvent> BpmGrid { get; set; } = new();
            public Func<long, double> TickToSec { get; set; } = _ => 0.0;
            public Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> Difficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public string SelectedTrackName { get; set; } = string.Empty;
            public int SelectedTrackPlayableNoteCount { get; set; }
        }

        public static bool IsLeadGuitarTrack(string? trackName) =>
            string.Equals(trackName?.Trim(), "PART GUITAR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trackName?.Trim(), "GUITAR", StringComparison.OrdinalIgnoreCase);

        public static ParsedMidi Parse(string midiPath)
        {
            if (!File.Exists(midiPath))
                throw new FileNotFoundException($"MIDI file not found: {midiPath}");

            byte[] bytes = File.ReadAllBytes(midiPath);
            int pos = 0;

            if (bytes.Length < 14 || Encoding.ASCII.GetString(bytes, 0, 4) != "MThd")
                throw new InvalidDataException("Invalid MIDI file header.");

            pos += 4;
            int headerLen = ReadInt32BE(bytes, ref pos);
            int format = ReadInt16BE(bytes, ref pos);
            int trackCount = ReadInt16BE(bytes, ref pos);
            int division = ReadInt16BE(bytes, ref pos);
            int ticksPerBeat = division > 0 ? division : 480;

            // Skip any extra header bytes
            if (headerLen > 6)
                pos += (headerLen - 6);

            var tempoEvents = new List<(long Tick, int MicrosecondsPerBeat, double Bpm)>();
            var tracks = new List<MidiTrack>();

            for (int t = 0; t < trackCount && pos < bytes.Length; t++)
            {
                if (pos + 8 > bytes.Length) break;
                string chunkId = Encoding.ASCII.GetString(bytes, pos, 4);
                pos += 4;
                int chunkLen = ReadInt32BE(bytes, ref pos);
                int endOfTrack = pos + chunkLen;
                if (endOfTrack > bytes.Length) endOfTrack = bytes.Length;

                if (chunkId != "MTrk")
                {
                    pos = endOfTrack;
                    continue;
                }

                long curTick = 0;
                byte runningStatus = 0;
                string trackName = string.Empty;
                var activeNotes = new Dictionary<int, long>();
                var trackNotes = new List<MidiNote>();

                while (pos < endOfTrack)
                {
                    long deltaTime = ReadVarLen(bytes, ref pos);
                    curTick += deltaTime;

                    if (pos >= endOfTrack) break;

                    byte b = bytes[pos];
                    byte status;
                    if ((b & 0x80) != 0)
                    {
                        status = b;
                        pos++;
                        if (status < 0xF0) runningStatus = status;
                    }
                    else
                    {
                        status = runningStatus;
                    }

                    if (status == 0xFF) // Meta Event
                    {
                        if (pos >= endOfTrack) break;
                        byte metaType = bytes[pos++];
                        int metaLen = (int)ReadVarLen(bytes, ref pos);
                        if (pos + metaLen > endOfTrack) metaLen = endOfTrack - pos;

                        if (metaType == 0x03) // Track Name
                        {
                            trackName = Encoding.UTF8.GetString(bytes, pos, metaLen).Trim();
                        }
                        else if (metaType == 0x51 && metaLen == 3) // Set Tempo
                        {
                            int tempo = (bytes[pos] << 16) | (bytes[pos + 1] << 8) | bytes[pos + 2];
                            if (tempo > 0)
                            {
                                double bpm = 60000000.0 / tempo;
                                tempoEvents.Add((curTick, tempo, bpm));
                            }
                        }

                        pos += metaLen;
                    }
                    else if (status == 0xF0 || status == 0xF7) // SysEx
                    {
                        int sysLen = (int)ReadVarLen(bytes, ref pos);
                        pos += Math.Min(sysLen, endOfTrack - pos);
                    }
                    else
                    {
                        byte cmd = (byte)(status & 0xF0);
                        if (cmd == 0x80 || cmd == 0x90) // Note Off / Note On
                        {
                            if (pos + 1 >= endOfTrack) break;
                            byte note = bytes[pos++];
                            byte vel = bytes[pos++];

                            bool isNoteOn = (cmd == 0x90 && vel > 0);
                            if (isNoteOn)
                            {
                                activeNotes[note] = curTick;
                            }
                            else
                            {
                                if (activeNotes.TryGetValue(note, out long startTick))
                                {
                                    activeNotes.Remove(note);
                                    long dur = curTick - startTick;
                                    trackNotes.Add(new MidiNote
                                    {
                                        NoteNumber = note,
                                        StartTick = startTick,
                                        DurationTicks = dur
                                    });
                                }
                            }
                        }
                        else if (cmd == 0xC0 || cmd == 0xD0)
                        {
                            pos += 1; // 1 data byte
                        }
                        else
                        {
                            pos += 2; // 2 data bytes (CC, pitch bend, etc.)
                        }
                    }
                }

                tracks.Add(new MidiTrack
                {
                    Name = trackName,
                    Notes = trackNotes
                });

                pos = endOfTrack;
            }

            // 1. Build Tempo Map & TickToSec
            tempoEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            if (tempoEvents.Count == 0 || tempoEvents[0].Tick != 0)
            {
                tempoEvents.Insert(0, (0, 500000, 120.0));
            }

            var timedTempo = new List<(long Tick, double TimeSec, int Tempo, double Bpm)>();
            var bpmGrid = new List<ChartParser.BpmEvent>();

            double tSec = 0.0;
            long prevTick = 0;
            int curTempo = 500000;

            foreach (var (evtTick, evtTempo, evtBpm) in tempoEvents)
            {
                long delta = evtTick - prevTick;
                tSec += (delta * (curTempo / 1000000.0)) / ticksPerBeat;
                timedTempo.Add((evtTick, tSec, evtTempo, evtBpm));
                bpmGrid.Add(new ChartParser.BpmEvent
                {
                    Tick = evtTick,
                    TimeSec = tSec,
                    Bpm = evtBpm
                });
                prevTick = evtTick;
                curTempo = evtTempo;
            }

            Func<long, double> tickToSec = tick =>
            {
                long lastEvtTick = 0;
                double lastT = 0.0;
                int lastTempo = 500000;
                foreach (var (evtTick, evtT, evtTmp, _) in timedTempo)
                {
                    if (tick <= evtTick)
                    {
                        long d = tick - lastEvtTick;
                        return lastT + (d * (lastTempo / 1000000.0)) / ticksPerBeat;
                    }
                    lastEvtTick = evtTick;
                    lastT = evtT;
                    lastTempo = evtTmp;
                }
                long delta = tick - lastEvtTick;
                return lastT + (delta * (lastTempo / 1000000.0)) / ticksPerBeat;
            };

            // 2. Find the track that actually contains chart pitches. Some
            // charts put an empty guitar-named metadata track before the notes;
            // selecting the first name match produced four empty difficulty files.
            var diffRanges = new Dictionary<string, (int Base, int Top)>
            {
                ["ExpertSingle"] = (96, 100),
                ["HardSingle"] = (84, 88),
                ["MediumSingle"] = (72, 76),
                ["EasySingle"] = (60, 64)
            };

            int PlayableMidiNoteCount(MidiTrack track) => track.Notes.Count(note =>
                diffRanges.Values.Any(range => note.NoteNumber >= range.Base && note.NoteNumber <= range.Top));

            MidiTrack? guitarTrack = tracks
                .Where(track => !string.IsNullOrWhiteSpace(track.Name) &&
                    (track.Name.IndexOf("GUITAR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     track.Name.IndexOf("PART GUITAR", StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(PlayableMidiNoteCount)
                .ThenByDescending(track => track.Name.IndexOf("PART GUITAR", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();

            if (guitarTrack == null || PlayableMidiNoteCount(guitarTrack) == 0)
            {
                guitarTrack = tracks.OrderByDescending(PlayableMidiNoteCount).FirstOrDefault()
                    ?? new MidiTrack();
            }

            var difficulties = new Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in diffRanges)
            {
                difficulties[kvp.Key] = new SortedDictionary<long, List<ChartParser.NoteEvent>>();
            }

            foreach (var note in guitarTrack.Notes)
            {
                foreach (var kvp in diffRanges)
                {
                    if (note.NoteNumber >= kvp.Value.Base && note.NoteNumber <= kvp.Value.Top)
                    {
                        int lane = note.NoteNumber - kvp.Value.Base;
                        var dict = difficulties[kvp.Key];
                        if (!dict.ContainsKey(note.StartTick))
                            dict[note.StartTick] = new List<ChartParser.NoteEvent>();

                        dict[note.StartTick].Add(new ChartParser.NoteEvent
                        {
                            Lane = lane,
                            DurationTicks = note.DurationTicks
                        });
                    }
                }
            }

            // MIDI uses per-difficulty force markers and a shared tap marker.
            // Markers may span a phrase, so apply their full tick range rather
            // than treating them as ordinary guitar notes.
            foreach (var kvp in diffRanges)
            {
                int noteBase = kvp.Value.Base;
                var hopoMarkers = guitarTrack.Notes.Where(note => note.NoteNumber == noteBase + 5).ToList();
                var strumMarkers = guitarTrack.Notes.Where(note => note.NoteNumber == noteBase + 6).ToList();
                var tapMarkers = guitarTrack.Notes.Where(note => note.NoteNumber == 104).ToList();
                bool CoversTick(MidiNote marker, long tick) => tick == marker.StartTick ||
                    (marker.DurationTicks > 0 && tick > marker.StartTick && tick < marker.StartTick + marker.DurationTicks);
                foreach (var entry in difficulties[kvp.Key])
                {
                    bool forcedHopo = hopoMarkers.Any(marker => CoversTick(marker, entry.Key));
                    bool forcedStrum = strumMarkers.Any(marker => CoversTick(marker, entry.Key));
                    bool tap = tapMarkers.Any(marker => CoversTick(marker, entry.Key));
                    foreach (var note in entry.Value)
                    {
                        note.ForceHopo = forcedHopo;
                        note.ForceStrum = forcedStrum;
                        note.Tap = tap;
                    }
                }
            }

            return new ParsedMidi
            {
                TicksPerBeat = ticksPerBeat,
                BpmGrid = bpmGrid,
                TickToSec = tickToSec,
                Difficulties = difficulties,
                SelectedTrackName = guitarTrack.Name,
                SelectedTrackPlayableNoteCount = PlayableMidiNoteCount(guitarTrack)
            };
        }

        private static int ReadInt32BE(byte[] data, ref int pos)
        {
            int val = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return val;
        }

        private static int ReadInt16BE(byte[] data, ref int pos)
        {
            int val = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            return val;
        }

        private static long ReadVarLen(byte[] data, ref int pos)
        {
            long val = 0;
            byte b;
            do
            {
                if (pos >= data.Length) break;
                b = data[pos++];
                val = (val << 7) | (b & 0x7Fu);
            } while ((b & 0x80) != 0);
            return val;
        }
    }
}
