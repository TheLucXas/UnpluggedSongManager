using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace USM.Core.Services
{
    public class AudioPipeline
    {
        private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string FfmpegSha256Url = FfmpegZipUrl + ".sha256";
        private static readonly SemaphoreSlim FfmpegInstallLock = new(1, 1);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
        private static string? _cachedFfmpegPath;

        private static string FfmpegInstallPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "USM", "tools", "ffmpeg", "bin", "ffmpeg.exe");

        private static readonly string[] AudioExtensions = { ".opus", ".ogg", ".mp3", ".wav", ".flac", ".m4a" };

        private static readonly Dictionary<string, string[]> StemNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["guitar"] = new[] { "guitar", "lead" },
            ["bass"] = new[] { "bass", "rhythm" },
            ["vocals"] = new[] { "vocals", "vocal" },
            ["other"] = new[] { "band", "crowd", "other", "keys", "keyboard", "instrumental", "backing" }
        };

        private static string? FindExistingFfmpeg()
        {
            if (!string.IsNullOrEmpty(_cachedFfmpegPath) && File.Exists(_cachedFfmpegPath))
                return _cachedFfmpegPath;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string besideApp = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(besideApp)) return _cachedFfmpegPath = besideApp;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string usmPath = FfmpegInstallPath;
            if (File.Exists(usmPath) && new FileInfo(usmPath).Length > 1024 * 1024)
                return _cachedFfmpegPath = usmPath;

            var current = new DirectoryInfo(appDir);
            for (int i = 0; i < 4 && current != null; i++)
            {
                string candidate = Path.Combine(current.FullName, "ffmpeg.exe");
                if (File.Exists(candidate)) return _cachedFfmpegPath = candidate;
                current = current.Parent;
            }

            string ffmpegIoPath = Path.Combine(appData, "ffmpegio", "ffmpeg-downloader", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(ffmpegIoPath)) return _cachedFfmpegPath = ffmpegIoPath;

            string? pathValue = Environment.GetEnvironmentVariable("PATH");
            foreach (string pathPart in (pathValue ?? string.Empty).Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(pathPart.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(candidate)) return _cachedFfmpegPath = candidate;
                }
                catch { }
            }

            return null;
        }

        private static async Task<string> EnsureFfmpegAsync(CancellationToken ct)
        {
            string? existing = FindExistingFfmpeg();
            if (existing != null) return existing;

            await FfmpegInstallLock.WaitAsync(ct);
            try
            {
                existing = FindExistingFfmpeg();
                if (existing != null) return existing;

                string exePath = FfmpegInstallPath;
                string installDir = Path.GetDirectoryName(exePath)!;
                Directory.CreateDirectory(installDir);

                string downloadPath = Path.Combine(installDir, $"ffmpeg-{Guid.NewGuid():N}.zip.download");
                string extractedPath = exePath + $".{Guid.NewGuid():N}.tmp";
                try
                {
#if IN_GAME_CHORUS
                    string checksumText = await Http.GetStringAsync(FfmpegSha256Url);
#else
                    string checksumText = await Http.GetStringAsync(FfmpegSha256Url, ct);
#endif
                    Match checksumMatch = Regex.Match(checksumText, @"\b[0-9a-fA-F]{64}\b");
                    if (!checksumMatch.Success)
                        throw new InvalidDataException("The FFmpeg download did not provide a valid SHA-256 checksum.");

                    using (var response = await Http.GetAsync(FfmpegZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength is long length && length > 500L * 1024 * 1024)
                            throw new InvalidDataException("The FFmpeg archive is larger than expected.");

#if IN_GAME_CHORUS
                        using var input = await response.Content.ReadAsStreamAsync();
#else
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
#endif
                        using var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                        await input.CopyToAsync(output, 1024 * 1024, ct);
                    }

                    using (var archiveStream = File.OpenRead(downloadPath))
                    {
#if IN_GAME_CHORUS
                        byte[] hash;
                        using (var sha = SHA256.Create()) hash = sha.ComputeHash(archiveStream);
                        string actualHash = BitConverter.ToString(hash).Replace("-", "");
#else
                        byte[] hash = await SHA256.HashDataAsync(archiveStream, ct);
                        string actualHash = Convert.ToHexString(hash);
#endif
                        string expectedHash = checksumMatch.Value;
                        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The downloaded FFmpeg archive failed its SHA-256 check.");
                    }

                    using (var archive = ZipFile.OpenRead(downloadPath))
                    {
                        var entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                            throw new InvalidDataException("The FFmpeg archive did not contain bin/ffmpeg.exe.");

                        using var source = entry.Open();
                        using var target = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                        await source.CopyToAsync(target, 1024 * 1024, ct);
                    }

#if IN_GAME_CHORUS
                    if (File.Exists(exePath)) File.Delete(exePath);
                    File.Move(extractedPath, exePath);
#else
                    File.Move(extractedPath, exePath, overwrite: true);
#endif
                    _cachedFfmpegPath = exePath;
                    return exePath;
                }
                finally
                {
                    try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { }
                    try { if (File.Exists(extractedPath)) File.Delete(extractedPath); } catch { }
                }
            }
            finally
            {
                FfmpegInstallLock.Release();
            }
        }

        public static bool IsFfmpegAvailable => FindExistingFfmpeg() != null;

        public static Task EnsureFfmpegInstalledAsync(CancellationToken ct = default)
        {
            return EnsureFfmpegAsync(ct);
        }

        public static async Task RunFfmpegAsync(string arguments, CancellationToken ct = default)
        {
            string ffmpegPath = await EnsureFfmpegAsync(ct);
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

#if IN_GAME_CHORUS
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
#else
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
#endif
            try
            {
#if IN_GAME_CHORUS
                await Task.Run(() => process.WaitForExit(), ct);
#else
                await process.WaitForExitAsync(ct);
#endif
            }
            catch (OperationCanceledException)
            {
#if IN_GAME_CHORUS
                try { process.Kill(); } catch { }
#else
                try { process.Kill(entireProcessTree: true); } catch { }
#endif
                throw;
            }

            string err = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg failed (exit code {process.ExitCode}): {err}");
        }

        public static async Task EncodeOrCopyAsync(string srcFile, string outFile, CancellationToken ct = default)
        {
            if (!File.Exists(srcFile)) return;
            if (srcFile.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) && IsVorbisOgg(srcFile))
                File.Copy(srcFile, outFile, overwrite: true);
            else
                await RunFfmpegAsync($"-y -threads 0 -i \"{srcFile}\" -b:a 192k \"{outFile}\"", ct);
        }

        private static bool IsVorbisOgg(string path)
        {
            byte[] header = new byte[128];
            using var source = File.OpenRead(path);
            int count = source.Read(header, 0, header.Length);
            if (count < 32 || header[0] != 'O' || header[1] != 'g' ||
                header[2] != 'g' || header[3] != 'S') return false;
            return System.Text.Encoding.ASCII.GetString(header, 0, count)
                .IndexOf("vorbis", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static async Task<(bool HasMultiTrack, bool IsHalfTrack)> ProcessAudioAsync(
            string srcDir,
            string outDir,
            double previewStart,
            Action<string>? log = null,
            CancellationToken ct = default)
        {
            var stems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var stemEntry in StemNames)
            {
                List<string> files = FindAudioFiles(srcDir, stemEntry.Value);
                if (files.Count > 0) stems[stemEntry.Key] = files;
            }

            var drums = Directory.GetFiles(srcDir)
                .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("drums", StringComparison.OrdinalIgnoreCase)
                    && AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (drums.Count > 0) stems["drums"] = drums;

            string? songAudio = FindAudioFile(srcDir, new[] { "song" });
            string[] audioFiles = Directory.GetFiles(srcDir)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            AudioTrackLayout layout = AudioLayoutDetector.Detect(audioFiles.Select(path => Path.GetFileName(path) ?? string.Empty));
            bool isHalfTrack = layout == AudioTrackLayout.HalfTrack;
            bool hasMultiTrack = layout is AudioTrackLayout.HalfTrack or AudioTrackLayout.MultiTrack;
            List<string>? vocalFiles = stems.TryGetValue("vocals", out var foundVocals) ? foundVocals : null;

            // In a two-track chart the game's guitar channel represents the
            // instrumental/backing source; the vocal channel remains untouched.
            if (isHalfTrack && vocalFiles is { Count: > 0 })
            {
                List<string> instrumentalFiles = stems
                    .Where(entry => !entry.Key.Equals("vocals", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(entry => entry.Value)
                    .ToList();
                if (instrumentalFiles.Count == 0 && songAudio != null)
                    instrumentalFiles.Add(songAudio);
                if (instrumentalFiles.Count == 0)
                {
                    var vocalFileSet = new HashSet<string>(vocalFiles!, StringComparer.OrdinalIgnoreCase);
                    instrumentalFiles.AddRange(audioFiles.Where(path => !vocalFileSet.Contains(path)
                        && !Path.GetFileNameWithoutExtension(path).StartsWith("sample", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileNameWithoutExtension(path).StartsWith("preview", StringComparison.OrdinalIgnoreCase)));
                }

                if (instrumentalFiles.Count == 0)
                {
                    log?.Invoke("  [WARN] The two-track layout was detected, but its instrumental source could not be found.");
                    isHalfTrack = false;
                    hasMultiTrack = false;
                    layout = AudioTrackLayout.SingleTrack;
                }
                else
                {
                    log?.Invoke("  [Audio] Detected one instrumental track and one vocal track; creating the game's 2-track layout automatically.");
                    string instrumentalPath = Path.Combine(outDir, "song-guitar.ogg");
                    string vocalPath = Path.Combine(outDir, "song-vocals.ogg");
                    await MixToOggAsync(instrumentalFiles, instrumentalPath, ct);
                    await MixToOggAsync(vocalFiles, vocalPath, ct);
                    await MixToOggAsync(new[] { instrumentalPath, vocalPath }, Path.Combine(outDir, "song.ogg"), ct);
                }
            }

            if (!isHalfTrack && layout == AudioTrackLayout.MultiTrack)
            {
                // song.opus is a master mix when chart stems are present. Do not
                // package it as another stem or it would bypass miss muting.
                var knownFiles = new HashSet<string>(stems.Values.SelectMany(files => files), StringComparer.OrdinalIgnoreCase);
                var unclassifiedFiles = audioFiles
                    .Where(path => !knownFiles.Contains(path)
                        && !string.Equals(path, songAudio, StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileNameWithoutExtension(path).StartsWith("sample", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileNameWithoutExtension(path).StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (unclassifiedFiles.Count > 0)
                {
                    if (!stems.TryGetValue("other", out var otherTracks))
                        stems["other"] = otherTracks = new List<string>();
                    otherTracks.AddRange(unclassifiedFiles);
                }

                if (stems.Count >= 2)
                {
                    log?.Invoke("  [Audio] Multiple independent chart stems detected; keeping their separate tracks.");
                    var tasks = stems.Select(async entry =>
                    {
                        string targetName = entry.Key switch
                        {
                            "guitar" => "song-guitar.ogg",
                            "bass" => "song-bass.ogg",
                            "vocals" => "song-vocals.ogg",
                            "drums" => "song-drums.ogg",
                            _ => "song-other.ogg"
                        };
                        string targetPath = Path.Combine(outDir, targetName);
                        await MixToOggAsync(entry.Value, targetPath, ct);
                        return targetPath;
                    });
                    var generatedStems = await Task.WhenAll(tasks);
                    await MixToOggAsync(generatedStems, Path.Combine(outDir, "song.ogg"), ct);
                }
                else
                {
                    string? fallbackMix = songAudio ?? audioFiles.FirstOrDefault();
                    if (fallbackMix != null)
                    {
                        log?.Invoke("  [WARN] The chart has multiple audio files, but its names cannot be mapped to independent game channels. Keeping the available mix as a single track.");
                        await EncodeOrCopyAsync(fallbackMix, Path.Combine(outDir, "song.ogg"), ct);
                    }
                    hasMultiTrack = false;
                }
            }

            if (!isHalfTrack && layout != AudioTrackLayout.MultiTrack)
            {
                string? audioSource = songAudio
                    ?? stems.Values.SelectMany(files => files).FirstOrDefault()
                    ?? audioFiles.FirstOrDefault();

                if (audioSource == null)
                {
                    log?.Invoke("  [WARN] No audio track found in chart.");
                }
                else
                {
                    log?.Invoke("  [Audio] One playable mix detected; keeping it as a single track.");
                    await EncodeOrCopyAsync(audioSource, Path.Combine(outDir, "song.ogg"), ct);
                    hasMultiTrack = false;
                }
            }

            string masterPath = Path.Combine(outDir, "song.ogg");
            if (File.Exists(masterPath))
            {
                string samplePath = Path.Combine(outDir, "sample.ogg");
                try
                {
                    await RunFfmpegAsync($"-y -threads 0 -ss {previewStart.ToString("F1", CultureInfo.InvariantCulture)} -t 25 -i \"{masterPath}\" -b:a 192k \"{samplePath}\"", ct);
                }
                catch
                {
                    await RunFfmpegAsync($"-y -threads 0 -ss 0 -t 25 -i \"{masterPath}\" -b:a 192k \"{samplePath}\"", ct);
                }
            }

            return (hasMultiTrack, isHalfTrack);
        }

        private static string? FindAudioFile(string directory, IEnumerable<string> baseNames)
        {
            foreach (string name in baseNames)
            {
                foreach (string extension in AudioExtensions)
                {
                    string candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static List<string> FindAudioFiles(string directory, IEnumerable<string> baseNames)
        {
            string[] names = baseNames.ToArray();
            return Directory.GetFiles(directory)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path =>
                {
                    string fileStem = Path.GetFileNameWithoutExtension(path);
                    return names.Any(name =>
                        fileStem.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || fileStem.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
                        || fileStem.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)
                        || fileStem.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                        || (fileStem.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                            && fileStem.Length > name.Length
                            && char.IsDigit(fileStem[name.Length]))
                        || (name.EndsWith("s", StringComparison.Ordinal) && fileStem.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            string[] coverNames = { "cover.jpg", "album.png", "album.jpeg", "cover.png", "cover.jpeg", "background.jpg", "background.png" };
            string? coverSrc = coverNames.Select(name => Path.Combine(srcDir, name)).FirstOrDefault(File.Exists);
            coverSrc ??= Directory.GetFiles(srcDir).FirstOrDefault(path =>
                path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            if (coverSrc == null) return;
            string destCover = Path.Combine(outDir, "cover.jpg");
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
