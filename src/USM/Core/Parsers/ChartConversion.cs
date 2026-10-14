using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace USM.Core.Parsers
{
    // Shared by the Windows manager and both in-game mods. Keep chart decisions
    // in one place so PC and Quest produce identical difficulties and note data.
    public static class ChartConversion
    {
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));

        public static Dictionary<string, string> GenerateJsonCharts(string inputDir, Action<string>? log = null)
        {
            return GenerateJsonCharts(inputDir, log, 0, out _, out _, out _, out _);
        }

        public static Dictionary<string, string> GenerateJsonCharts(
            string inputDir,
            Action<string>? log,
            double songDurationSeconds,
            out int sourceDifficultyCount,
            out int sourceNoteCount,
            out double chartCoverageRatio,
            out bool hasLeadGuitarChart)
        {
            sourceDifficultyCount = 0;
            sourceNoteCount = 0;
            chartCoverageRatio = 0;
            hasLeadGuitarChart = false;
            string midPath = Path.Combine(inputDir, "notes.mid");
            if (!File.Exists(midPath)) midPath = Path.Combine(inputDir, "notes.midi");
            string chartPath = Path.Combine(inputDir, "notes.chart");
            if (!File.Exists(chartPath)) chartPath = Path.Combine(inputDir, "song.chart");

            var candidates = new List<(string Name, List<ChartParser.BpmEvent> BpmGrid, Func<long, double> TickToSec,
                Dictionary<string, SortedDictionary<long, List<ChartParser.NoteEvent>>> Difficulties, int TicksPerBeat, int HopoThresholdTicks, bool HasLeadGuitarChart)>();

            if (File.Exists(midPath))
            {
                try
                {
                    log?.Invoke("  [Chart] Reading notes.mid (Standard MIDI format).");
                    var midi = MidiParser.Parse(midPath);
                    log?.Invoke($"  [Chart] MIDI guitar track '{midi.SelectedTrackName}' contains {midi.SelectedTrackPlayableNoteCount} notes in supported difficulty ranges.");
                    candidates.Add(("notes.mid", midi.BpmGrid, midi.TickToSec, midi.Difficulties, midi.TicksPerBeat, midi.HopoThresholdTicks,
                        MidiParser.IsLeadGuitarTrack(midi.SelectedTrackName)));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [Chart] Could not parse notes.mid: {ex.Message}");
                }
            }

            if (File.Exists(chartPath))
            {
                try
                {
                    log?.Invoke($"  [Chart] Reading {Path.GetFileName(chartPath)} (Clone Hero text format).");
                    var chart = ChartParser.Parse(chartPath);
                    if (chart.IgnoredNonPlayableNoteEvents > 0)
                    {
                        log?.Invoke($"  [Chart] Ignored {chart.IgnoredNonPlayableNoteEvents} non-playable note events (HOPO/tap markers or unsupported lanes).");
                    }
                    candidates.Add((Path.GetFileName(chartPath), chart.BpmGrid, chart.TickToSec, chart.Difficulties, chart.Resolution, chart.HopoThresholdTicks,
                        chart.Difficulties.Count > 0));
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [Chart] Could not parse {Path.GetFileName(chartPath)}: {ex.Message}");
                }
            }

            var selected = candidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    DifficultyCount = candidate.Difficulties.Count(entry => entry.Value.Values.Any(group => group != null && group.Count > 0)),
                    NoteCount = candidate.Difficulties.Values.Sum(notes => notes.Values.Where(group => group != null).Sum(group => group.Count)),
                    IsTextChart = candidate.Name.EndsWith(".chart", StringComparison.OrdinalIgnoreCase),
                    NoteTimes = candidate.Difficulties.Values
                        .SelectMany(notes => notes.Keys)
                        .Select(candidate.TickToSec)
                        .Where(time => IsFinite(time) && time >= 0)
                        .ToList()
                })
                .Select(candidate => new
                {
                    candidate.Candidate,
                    candidate.DifficultyCount,
                    candidate.NoteCount,
                    candidate.IsTextChart,
                    candidate.NoteTimes,
                    FirstNoteTime = candidate.NoteTimes.Count > 0 ? candidate.NoteTimes.Min() : 0,
                    LastNoteTime = candidate.NoteTimes.Count > 0 ? candidate.NoteTimes.Max() : 0,
                    Coverage = songDurationSeconds > 0 && candidate.NoteTimes.Count > 0
                        ? Clamp((candidate.NoteTimes.Max() - candidate.NoteTimes.Min()) / songDurationSeconds, 0, 1)
                        : 0,
                    EndCoverage = songDurationSeconds > 0 && candidate.NoteTimes.Count > 0
                        ? Clamp(candidate.NoteTimes.Max() / songDurationSeconds, 0, 1)
                        : 0
                })
                .Where(candidate => candidate.NoteCount > 0)
                // Prefer the chart that spans the song. Chorus packages can
                // contain auxiliary/truncated MIDI and text charts; choosing
                // solely by difficulty count or note count hid long openings
                // (for example, charts whose first note was around 139 seconds).
                .OrderByDescending(candidate => songDurationSeconds > 0 ? candidate.Coverage : 0)
                .ThenByDescending(candidate => songDurationSeconds > 0 ? candidate.EndCoverage : 0)
                .ThenByDescending(candidate => candidate.DifficultyCount)
                .ThenByDescending(candidate => candidate.NoteCount)
                .ThenByDescending(candidate => candidate.IsTextChart)
                .FirstOrDefault();

            if (selected == null)
                throw new FileNotFoundException("No playable guitar notes were found in notes.chart, song.chart, or notes.mid.");

            sourceDifficultyCount = Math.Min(4, JsonGenerator.GetUsableSourceDifficultyNames(
                selected.Candidate.Difficulties, selected.Candidate.TickToSec).Count);
            sourceNoteCount = selected.NoteCount;
            hasLeadGuitarChart = selected.Candidate.HasLeadGuitarChart;
            double firstNoteTime = selected.FirstNoteTime;
            double lastNoteTime = selected.LastNoteTime;
            chartCoverageRatio = selected.Coverage;

            log?.Invoke($"  [Chart] Using {selected.Candidate.Name}: {sourceDifficultyCount} usable source difficulties ({selected.DifficultyCount} populated), {selected.NoteCount} note events; range {firstNoteTime:F1}-{lastNoteTime:F1}s of {songDurationSeconds:F1}s ({chartCoverageRatio:P0} coverage).");
            if (songDurationSeconds > 0 && (chartCoverageRatio < 0.50 || firstNoteTime > songDurationSeconds * 0.25))
                log?.Invoke("  [ChartWarning] The best available chart has suspiciously low song coverage or a late first note. The source chart may be incomplete; check the generated first-note time before playing.");
            return JsonGenerator.GenerateAllDifficultiesJson(
                selected.Candidate.BpmGrid,
                selected.Candidate.TickToSec,
                selected.Candidate.Difficulties,
                selected.Candidate.TicksPerBeat,
                log,
                songDurationSeconds,
                selected.Candidate.HopoThresholdTicks);
        }

    }
}
