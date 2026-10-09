using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Models;

namespace USM.Core.Services
{
    public class AudioPipeline
    {
        private static string? _cachedFfmpegPath;

        public static string FindFfmpeg()
        {
            if (!string.IsNullOrEmpty(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
                return _cachedFfmpegPath;

            // 1. Next to executing assembly
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string p1 = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(p1)) return _cachedFfmpegPath = p1;

            // 2. Project root / parent directories
            var cur = new DirectoryInfo(appDir);
            for (int i = 0; i < 4 && cur != null; i++)
            {
                string candidate = Path.Combine(cur.FullName, "ffmpeg.exe");
                if (File.Exists(candidate)) return _cachedFfmpegPath = candidate;
                cur = cur.Parent;
            }

            // 3. User local AppData ffmpegio
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string p3 = Path.Combine(localAppData, @"ffmpegio\ffmpeg-downloader\ffmpeg\bin\ffmpeg.exe");
            if (File.Exists(p3)) return _cachedFfmpegPath = p3;

            // 4. Fallback to system PATH
            return _cachedFfmpegPath = "ffmpeg";
        }

        public static async Task RunFfmpegAsync(string arguments, CancellationToken ct = default)
        {
            string ffmpegPath = FindFfmpeg();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-nostats -loglevel error {arguments}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            string err = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg failed (exit code {process.ExitCode}): {err}");
            }
        }

        public static async Task EncodeOrCopyAsync(string srcFile, string outFile, CancellationToken ct = default)
        {
            if (!File.Exists(srcFile)) return;

            if (srcFile.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(srcFile, outFile, overwrite: true);
            }
            else
            {
                await RunFfmpegAsync($"-y -threads 0 -i \"{srcFile}\" -b:a 192k \"{outFile}\"", ct);
            }
        }

        public static async Task<(bool HasMultiTrack, bool IsHalfTrack)> ProcessAudioAsync(
            string srcDir,
            string outDir,
            double previewStart,
            AudioLayoutMode audioLayout = AudioLayoutMode.FullMultitrack,
            Action<string>? log = null,
            CancellationToken ct = default)
        {
            var audioExts = new[] { ".opus", ".ogg", ".mp3", ".wav", ".flac", ".m4a" };

            var stemsFound = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            var stemMap = new Dictionary<string, string[]>
            {
                ["guitar"] = new[] { "guitar.ogg", "guitar.opus", "guitar.ogg", "guitar.wav" },
                ["bass"] = new[] { "bass.ogg", "bass.opus", "bass.ogg", "rhythm.ogg", "rhythm.opus", "rhythm.ogg" },
                ["vocals"] = new[] { "vocals.ogg", "vocals.opus", "vocals.ogg", "vocals.wav" },
                // song.ogg is usually the full stereo mix, not an independent stem.
                ["other"] = new[] { "band.ogg", "band.opus", "crowd.ogg" }
            };

            foreach (var kvp in stemMap)
            {
                foreach (var fn in kvp.Value)
                {
                    string p = Path.Combine(srcDir, fn);
                    if (File.Exists(p))
                    {
                        stemsFound[kvp.Key] = p;
                        break;
                    }
                }
            }

            // Drums detection (supports single or multiple drum stem files)
            var drumFiles = Directory.GetFiles(srcDir)
                .Where(f =>
                {
                    string name = Path.GetFileName(f).ToLowerInvariant();
                    return name.StartsWith("drums") && audioExts.Any(ext => name.EndsWith(ext));
                })
                .ToList();

            if (drumFiles.Count > 0)
            {
                stemsFound["drums"] = drumFiles;
            }

            bool hasMultiTrack = stemsFound.ContainsKey("guitar") && stemsFound.Count > 1;
            bool isHalfTrack = false;

            if (audioLayout == AudioLayoutMode.InstrumentalAndVocals && !hasMultiTrack)
            {
                log?.Invoke("  [Audio] 2-track layout unavailable: this chart has no separate instrumental stems. Keeping the single stereo mix.");
            }

            if (hasMultiTrack && audioLayout == AudioLayoutMode.InstrumentalAndVocals)
            {
                if (stemsFound.TryGetValue("vocals", out var vocalSource))
                {
                    var instrumentalSources = new List<string>();
                    foreach (string key in new[] { "guitar", "bass", "drums", "other" })
                    {
                        if (!stemsFound.TryGetValue(key, out var source)) continue;
                        instrumentalSources.AddRange(GetSourceFiles(source)
                            .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), "song", StringComparison.OrdinalIgnoreCase)));
                    }

                    if (instrumentalSources.Count > 0)
                    {
                        log?.Invoke("  [Audio] Creating 2-track layout: all instrumental stems mixed together, vocals kept separate...");
                        string instrumentalPath = Path.Combine(outDir, "song-guitar.ogg");
                        string vocalsPath = Path.Combine(outDir, "song-vocals.ogg");
                        await MixToOggAsync(instrumentalSources, instrumentalPath, ct);
                        await EncodeOrCopyAsync(GetSourceFiles(vocalSource).First(), vocalsPath, ct);

                        string masterPath = Path.Combine(outDir, "song.ogg");
                        string? originalMaster = new[] { "song.ogg", "song.opus", "song.mp3", "song.wav" }
                            .Select(name => Path.Combine(srcDir, name))
                            .FirstOrDefault(File.Exists);
                        if (originalMaster != null)
                        {
                            await EncodeOrCopyAsync(originalMaster, masterPath, ct);
                        }
                        else
                        {
                            await MixToOggAsync(new[] { instrumentalPath, vocalsPath }, masterPath, ct);
                        }
                        isHalfTrack = true;
                    }
                    else
                    {
                        log?.Invoke("  [Audio] 2-track layout unavailable: no separate instrumental stems were found. Keeping the full source layout.");
                    }
                }
                else
                {
                    log?.Invoke("  [Audio] 2-track layout unavailable: no separate vocals stem was found. Keeping the full source layout.");
                }
            }

            if (hasMultiTrack && !isHalfTrack)
            {
                log?.Invoke("  [Audio] Multitrack stems detected. Encoding channels in parallel...");

                var tasks = new List<(string Name, object Source, string TargetFile)>();
                if (stemsFound.TryGetValue("guitar", out var gSrc)) tasks.Add(("guitar", gSrc, "song-guitar.ogg"));
                if (stemsFound.TryGetValue("bass", out var bSrc)) tasks.Add(("bass", bSrc, "song-bass.ogg"));
                if (stemsFound.TryGetValue("vocals", out var vSrc)) tasks.Add(("vocals", vSrc, "song-vocals.ogg"));
                if (stemsFound.TryGetValue("drums", out var dSrc)) tasks.Add(("drums", dSrc, "song-drums.ogg"));
                if (stemsFound.TryGetValue("other", out var oSrc)) tasks.Add(("other", oSrc, "song-other.ogg"));

                var encodeTasks = tasks.Select(t => Task.Run(async () =>
                {
                    string outPath = Path.Combine(outDir, t.TargetFile);
                    if (t.Source is List<string> list)
                    {
                        if (list.Count > 1)
                        {
                            var inputs = string.Join(" ", list.Select(f => $"-i \"{f}\""));
                            await RunFfmpegAsync($"-y -threads 0 {inputs} -filter_complex \"amix=inputs={list.Count}:dropout_transition=0:normalize=0\" -b:a 192k \"{outPath}\"", ct);
                        }
                        else if (list.Count == 1)
                        {
                            await EncodeOrCopyAsync(list[0], outPath, ct);
                        }
                    }
                    else if (t.Source is string singleFile)
                    {
                        await EncodeOrCopyAsync(singleFile, outPath, ct);
                    }
                }, ct)).ToArray();

                await Task.WhenAll(encodeTasks);

                // Mixdown all generated stems into song.ogg
                var generatedStems = tasks
                    .Select(t => Path.Combine(outDir, t.TargetFile))
                    .Where(File.Exists)
                    .ToList();

                string masterPath = Path.Combine(outDir, "song.ogg");
                if (generatedStems.Count > 1)
                {
                    var inputs = string.Join(" ", generatedStems.Select(f => $"-i \"{f}\""));
                    await RunFfmpegAsync($"-y -threads 0 {inputs} -filter_complex \"amix=inputs={generatedStems.Count}:dropout_transition=0:normalize=0\" -b:a 192k \"{masterPath}\"", ct);
                }
                else if (generatedStems.Count == 1)
                {
                    File.Copy(generatedStems[0], masterPath, overwrite: true);
                }
            }
            else
            {
                log?.Invoke("  [Audio] Standard stereo audio track detected. Converting...");
                string? audioSrc = null;

                string[] fullMixCandidates = { "song.ogg", "song.opus", "song.mp3", "song.wav" };
                foreach (string candidate in fullMixCandidates)
                {
                    string candidatePath = Path.Combine(srcDir, candidate);
                    if (File.Exists(candidatePath))
                    {
                        audioSrc = candidatePath;
                        break;
                    }
                }

                if (audioSrc == null && stemsFound.Count == 1)
                {
                    var single = stemsFound.Values.First();
                    if (single is string s && File.Exists(s)) audioSrc = s;
                    else if (single is List<string> l && l.Count > 0 && File.Exists(l[0])) audioSrc = l[0];
                }

                if (audioSrc == null)
                {
                    string[] fallbacks = {
                        "song.opus", "song.ogg", "song.ogg", "song.wav",
                        "guitar.opus", "guitar.ogg", "guitar.ogg", "guitar.wav",
                        "rhythm.opus", "rhythm.ogg", "rhythm.ogg"
                    };
                    foreach (var fn in fallbacks)
                    {
                        string p = Path.Combine(srcDir, fn);
                        if (File.Exists(p)) { audioSrc = p; break; }
                    }
                }

                if (audioSrc == null)
                {
                    audioSrc = Directory.GetFiles(srcDir)
                        .FirstOrDefault(f => audioExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                }

                string masterPath = Path.Combine(outDir, "song.ogg");
                if (audioSrc != null)
                {
                    await EncodeOrCopyAsync(audioSrc, masterPath, ct);
                }
                else
                {
                    log?.Invoke("  [WARN] No audio track found in chart.");
                }
            }

            // Generate sample.ogg preview
            string masterMp3 = Path.Combine(outDir, "song.ogg");
            if (File.Exists(masterMp3))
            {
                string samplePath = Path.Combine(outDir, "sample.ogg");
                try
                {
                    await RunFfmpegAsync($"-y -threads 0 -ss {previewStart:F1} -t 25 -i \"{masterMp3}\" -b:a 192k \"{samplePath}\"", ct);
                }
                catch
                {
                    // Fallback to beginning if previewStart was past end
                    await RunFfmpegAsync($"-y -threads 0 -ss 0 -t 25 -i \"{masterMp3}\" -b:a 192k \"{samplePath}\"", ct);
                }
            }

            return (hasMultiTrack, isHalfTrack);
        }

        private static IEnumerable<string> GetSourceFiles(object source)
        {
            if (source is string single && File.Exists(single))
            {
                yield return single;
            }
            else if (source is List<string> files)
            {
                foreach (string file in files)
                {
                    if (File.Exists(file)) yield return file;
                }
            }
        }

        private static async Task MixToOggAsync(IEnumerable<string> sourceFiles, string outputPath, CancellationToken ct)
        {
            var files = sourceFiles.Where(File.Exists).ToList();
            if (files.Count == 0) throw new FileNotFoundException("No audio inputs were available for mixing.");
            if (files.Count == 1)
            {
                await EncodeOrCopyAsync(files[0], outputPath, ct);
                return;
            }

            string inputs = string.Join(" ", files.Select(file => $"-i \"{file}\""));
            await RunFfmpegAsync(
                $"-y -threads 0 {inputs} -filter_complex \"amix=inputs={files.Count}:dropout_transition=0:normalize=0\" -b:a 192k \"{outputPath}\"",
                ct);
        }

        public static async Task ProcessCoverAsync(string srcDir, string outDir, CancellationToken ct = default)
        {
            string[] coverNames = {
                "cover.jpg", "album.png", "album.jpeg",
                "cover.jpg", "cover.png", "cover.jpeg",
                "background.jpg", "background.png"
            };

            string? coverSrc = null;
            foreach (var cn in coverNames)
            {
                string p = Path.Combine(srcDir, cn);
                if (File.Exists(p)) { coverSrc = p; break; }
            }

            coverSrc ??= Directory.GetFiles(srcDir)
                .FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            string destCover = Path.Combine(outDir, "cover.jpg");
            if (coverSrc != null)
            {
                try
                {
                    await RunFfmpegAsync($"-y -i \"{coverSrc}\" -vf scale=512:512 -q:v 2 \"{destCover}\"", ct);
                }
                catch
                {
                    File.Copy(coverSrc, destCover, overwrite: true);
                }
            }
        }
    }
}
