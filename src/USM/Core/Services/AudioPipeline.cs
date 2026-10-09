using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            ["guitar"] = new[] { "guitar" },
            ["bass"] = new[] { "bass", "rhythm" },
            ["vocals"] = new[] { "vocals", "vocal" },
            ["other"] = new[] { "band", "crowd", "other" }
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
            foreach (string pathPart in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
                    string checksumText = await Http.GetStringAsync(FfmpegSha256Url, ct);
                    Match checksumMatch = Regex.Match(checksumText, @"\b[0-9a-fA-F]{64}\b");
                    if (!checksumMatch.Success)
                        throw new InvalidDataException("The FFmpeg download did not provide a valid SHA-256 checksum.");

                    using (var response = await Http.GetAsync(FfmpegZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength is long length && length > 500L * 1024 * 1024)
                            throw new InvalidDataException("The FFmpeg archive is larger than expected.");

                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        await using var output = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                        await input.CopyToAsync(output, 1024 * 1024, ct);
                    }

                    await using (var archiveStream = File.OpenRead(downloadPath))
                    {
                        byte[] hash = await SHA256.HashDataAsync(archiveStream, ct);
                        string actualHash = Convert.ToHexString(hash);
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

                        await using var source = entry.Open();
                        await using var target = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                        await source.CopyToAsync(target, 1024 * 1024, ct);
                    }

                    File.Move(extractedPath, exePath, overwrite: true);
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

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
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
            if (srcFile.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                File.Copy(srcFile, outFile, overwrite: true);
            else
                await RunFfmpegAsync($"-y -threads 0 -i \"{srcFile}\" -b:a 192k \"{outFile}\"", ct);
        }

        public static async Task<(bool HasMultiTrack, bool IsHalfTrack)> ProcessAudioAsync(
            string srcDir,
            string outDir,
            double previewStart,
            Action<string>? log = null,
            CancellationToken ct = default)
        {
            var stems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (stem, names) in StemNames)
            {
                string? file = FindAudioFile(srcDir, names);
                if (file != null) stems[stem] = new List<string> { file };
            }

            var drums = Directory.GetFiles(srcDir)
                .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("drums", StringComparison.OrdinalIgnoreCase)
                    && AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (drums.Count > 0) stems["drums"] = drums;

            string? songAudio = FindAudioFile(srcDir, new[] { "song" });
            List<string> instrumentalStems = stems
                .Where(entry => !entry.Key.Equals("vocals", StringComparison.OrdinalIgnoreCase))
                .SelectMany(entry => entry.Value)
                .ToList();
            string? vocals = stems.TryGetValue("vocals", out var vocalFiles) ? vocalFiles.FirstOrDefault() : null;
            int sourceTrackCount = instrumentalStems.Count + (vocals == null ? 0 : vocalFiles!.Count) + (songAudio == null ? 0 : 1);

            bool isHalfTrack = false;
            bool hasMultiTrack = false;

            // A chart with one vocal stem and one backing/instrumental source is
            // represented in-game as guitar + vocals so misses only mute the backing.
            if (vocals != null && sourceTrackCount == 2)
            {
                string instrumental = instrumentalStems.Count == 1 ? instrumentalStems[0] : songAudio!;
                log?.Invoke("  [Audio] Detected one instrumental track and one vocal track; creating the game's 2-track layout automatically.");
                string instrumentalPath = Path.Combine(outDir, "song-guitar.ogg");
                string vocalPath = Path.Combine(outDir, "song-vocals.ogg");
                await EncodeOrCopyAsync(instrumental, instrumentalPath, ct);
                await EncodeOrCopyAsync(vocals, vocalPath, ct);
                await MixToOggAsync(new[] { instrumentalPath, vocalPath }, Path.Combine(outDir, "song.ogg"), ct);
                hasMultiTrack = true;
                isHalfTrack = true;
            }
            else
            {
                // In Clone Hero style packages, song.ogg is the background mix
                // when separate instrument stems are present. Preserve it as the
                // game's Other track instead of losing it or flattening the stems.
                if (songAudio != null && stems.Count > 0)
                {
                    if (!stems.TryGetValue("other", out var otherTracks))
                        stems["other"] = otherTracks = new List<string>();
                    otherTracks.Add(songAudio);
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
                    hasMultiTrack = true;
                }
                else
                {
                    string? audioSource = songAudio
                        ?? stems.Values.SelectMany(files => files).FirstOrDefault()
                        ?? Directory.GetFiles(srcDir).FirstOrDefault(path =>
                            AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

                    if (audioSource == null)
                    {
                        log?.Invoke("  [WARN] No audio track found in chart.");
                    }
                    else
                    {
                        log?.Invoke("  [Audio] One playable mix detected; keeping it as a single track.");
                        await EncodeOrCopyAsync(audioSource, Path.Combine(outDir, "song.ogg"), ct);
                    }
                }
            }

            string masterPath = Path.Combine(outDir, "song.ogg");
            if (File.Exists(masterPath))
            {
                string samplePath = Path.Combine(outDir, "sample.ogg");
                try
                {
                    await RunFfmpegAsync($"-y -threads 0 -ss {previewStart:F1} -t 25 -i \"{masterPath}\" -b:a 192k \"{samplePath}\"", ct);
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
