using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services;

namespace USM.Core.Parsers
{
    public class SngDecoder
    {
        private const int InitialRangeSize = 64 * 1024;
        private const int MaximumIndexSize = 8 * 1024 * 1024;
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        static SngDecoder()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("USM/1.0 (VR Rhythm Game Mod Tool)");
        }

        public class SngFileEntry
        {
            public string FileName { get; set; } = string.Empty;
            public ulong ContentLength { get; set; }
            public ulong ContentIndex { get; set; }
        }

        private sealed class SngPackageIndex
        {
            public byte[] XorMask { get; init; } = Array.Empty<byte>();
            public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public List<SngFileEntry> Files { get; init; } = new();
        }

        public sealed class RemoteSongProfile
        {
            public AudioTrackLayout TrackLayout { get; init; } = AudioTrackLayout.Unknown;
            public int AudioStemCount { get; init; }
            public bool HasVocalsStem { get; init; }
            public bool HasGuitarStem { get; init; }
            public bool HasLeadGuitarChart { get; init; }
            public bool HasChartProfile { get; init; }
            public int SourceDifficultyCount { get; init; }
            public int ChartNoteCount { get; init; }
            public double SongDurationSeconds { get; init; }
            public double ChartCoverageRatio { get; init; }
            public HashSet<string> StemGroups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public bool IsIdealForUnplugged => TrackLayout == AudioTrackLayout.MultiTrack
                && new[] { "guitar", "bass", "drums", "vocals" }.All(StemGroups.Contains)
                && HasLeadGuitarChart
                && SourceDifficultyCount >= 4
                && ChartNoteCount > 0
                && ChartCoverageRatio >= 0.90;
        }

        /// <summary>
        /// Reads only the SNG package index and infers its audio layout from the
        /// listed filenames. It never downloads audio or video payloads.
        /// </summary>
        public static async Task<AudioTrackLayout> ProbeRemoteAudioLayoutAsync(string sngUrl, CancellationToken ct = default)
        {
            if (!Uri.TryCreate(sngUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("files.enchor.us", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Only HTTPS SNG downloads from files.enchor.us are supported.");
            }

            SngPackageIndex? index = await ReadRemotePackageIndexAsync(uri, ct);
            if (index == null)
                return AudioTrackLayout.Unknown;

            return AudioLayoutDetector.Detect(index.Files.Select(entry => entry.FileName));
        }

        /// <summary>
        /// Inspects the SNG index and downloads only chart note files with byte
        /// ranges. It never downloads audio or video payloads for this check.
        /// If the server ignores range requests, it returns the index-derived
        /// audio layout and leaves chart quality unknown.
        /// </summary>
        public static async Task<RemoteSongProfile> ProbeRemoteSongProfileAsync(
            string sngUrl,
            CancellationToken ct = default,
            int songDurationSeconds = 0)
        {
            if (!Uri.TryCreate(sngUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("files.enchor.us", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Only HTTPS SNG downloads from files.enchor.us are supported.");
            }

            SngPackageIndex? index = await ReadRemotePackageIndexAsync(uri, ct);
            if (index == null)
                return new RemoteSongProfile();

            var audio = AudioLayoutDetector.Analyze(index.Files.Select(entry => entry.FileName));
            var profile = new RemoteSongProfile
            {
                TrackLayout = audio.Layout,
                AudioStemCount = audio.StemCount,
                HasVocalsStem = audio.HasVocals,
                HasGuitarStem = audio.StemGroups.Contains("guitar"),
                StemGroups = audio.StemGroups
            };

            double durationSeconds = songDurationSeconds > 0
                ? songDurationSeconds
                : ReadDurationFromMetadata(index.Metadata);

            var chartEntries = index.Files
                .Where(entry =>
                {
                    string name = Path.GetFileName(entry.FileName);
                    string extension = Path.GetExtension(name);
                    return extension.Equals(".chart", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".mid", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".midi", StringComparison.OrdinalIgnoreCase);
                })
                .Where(entry => entry.ContentLength > 0 && entry.ContentLength <= 8UL * 1024 * 1024)
                .ToList();

            if (chartEntries.Count == 0)
                return profile;

            string tempDir = Path.Combine(Path.GetTempPath(), $"usm_chart_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                int bestDifficultyCount = 0;
                int bestNoteCount = 0;
                bool bestHasLeadGuitarChart = false;
                bool hasParsedChartProfile = false;
                double bestFirstNoteTime = 0;
                double bestLastNoteTime = 0;
                double bestCoverageRatio = -1;
                foreach (SngFileEntry entry in chartEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[]? chartBytes = await ReadRemoteEntryBytesAsync(uri, index, entry, ct);
                    if (chartBytes == null || chartBytes.Length == 0)
                        continue;

                    string name = Path.GetFileName(entry.FileName);
                    string path = Path.Combine(tempDir, name);
                    await File.WriteAllBytesAsync(path, chartBytes, ct);
                    try
                    {
                        int difficultyCount;
                        int noteCount;
                        bool hasLeadGuitarChart;
                        List<double> noteTimes;
                        string extension = Path.GetExtension(name);
                        if (extension.Equals(".chart", StringComparison.OrdinalIgnoreCase))
                        {
                            var parsed = ChartParser.Parse(path);
                            difficultyCount = JsonGenerator.GetUsableSourceDifficultyNames(
                                parsed.Difficulties, parsed.TickToSec).Count;
                            noteCount = parsed.Difficulties.Values.Sum(notes => notes.Values.Sum(group => group.Count));
                            hasLeadGuitarChart = noteCount > 0;
                            noteTimes = parsed.Difficulties.Values.SelectMany(notes => notes.Keys)
                                .Select(parsed.TickToSec).Where(time => double.IsFinite(time) && time >= 0).ToList();
                        }
                        else
                        {
                            var parsed = MidiParser.Parse(path);
                            difficultyCount = JsonGenerator.GetUsableSourceDifficultyNames(
                                parsed.Difficulties, parsed.TickToSec).Count;
                            noteCount = parsed.Difficulties.Values.Sum(notes => notes.Values.Sum(group => group.Count));
                            hasLeadGuitarChart = MidiParser.IsLeadGuitarTrack(parsed.SelectedTrackName);
                            noteTimes = parsed.Difficulties.Values.SelectMany(notes => notes.Keys)
                                .Select(parsed.TickToSec).Where(time => double.IsFinite(time) && time >= 0).ToList();
                        }

                        hasParsedChartProfile = true;

                        double firstNoteTime = noteTimes.Count > 0 ? noteTimes.Min() : 0;
                        double lastNoteTime = noteTimes.Count > 0 ? noteTimes.Max() : 0;
                        double candidateCoverage = durationSeconds > 0 && noteTimes.Count > 0
                            ? Math.Clamp((lastNoteTime - firstNoteTime) / durationSeconds, 0, 1)
                            : -1;
                        bool better = durationSeconds > 0
                            ? candidateCoverage > bestCoverageRatio
                                || (Math.Abs(candidateCoverage - bestCoverageRatio) < 0.0001
                                    && (difficultyCount > bestDifficultyCount
                                        || (difficultyCount == bestDifficultyCount && noteCount > bestNoteCount)))
                            : difficultyCount > bestDifficultyCount
                                || (difficultyCount == bestDifficultyCount && noteCount > bestNoteCount);

                        if (better)
                        {
                            bestDifficultyCount = difficultyCount;
                            bestNoteCount = noteCount;
                            bestHasLeadGuitarChart = hasLeadGuitarChart;
                            bestFirstNoteTime = firstNoteTime;
                            bestLastNoteTime = lastNoteTime;
                            bestCoverageRatio = candidateCoverage;
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Try another chart file in the package. Chorus SNGs can
                        // contain auxiliary chart files that are not playable guitar charts.
                    }
                }

                double coverageRatio = durationSeconds > 0 && bestNoteCount > 0 && bestCoverageRatio >= 0
                    ? bestCoverageRatio
                    : 0;

                return new RemoteSongProfile
                {
                    TrackLayout = profile.TrackLayout,
                    AudioStemCount = profile.AudioStemCount,
                    HasVocalsStem = profile.HasVocalsStem,
                    HasGuitarStem = profile.HasGuitarStem,
                    HasLeadGuitarChart = bestHasLeadGuitarChart,
                    HasChartProfile = hasParsedChartProfile,
                    StemGroups = profile.StemGroups,
                    SourceDifficultyCount = Math.Min(4, bestDifficultyCount),
                    ChartNoteCount = bestNoteCount,
                    SongDurationSeconds = durationSeconds,
                    ChartCoverageRatio = coverageRatio
                };
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static double ReadDurationFromMetadata(Dictionary<string, string> metadata)
        {
            foreach (var pair in metadata)
            {
                if (!pair.Key.Equals("song_length", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Equals("duration", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Equals("length", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
                    continue;

                return value > 1000 ? value / 1000.0 : value;
            }

            return 0;
        }

        /// <summary>
        /// Downloads just the SNG package index and non-video file payloads using
        /// HTTP range requests. This prevents large video backgrounds from being
        /// transferred when importing charts from Chorus.
        /// </summary>
        public static async Task DecodeRemoteAsync(
            string sngUrl,
            string outputDir,
            IProgress<double>? progress = null,
            Func<string, bool>? includeFile = null,
            CancellationToken ct = default)
        {
            if (!Uri.TryCreate(sngUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("files.enchor.us", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Only HTTPS SNG downloads from files.enchor.us are supported.");
            }

            Directory.CreateDirectory(outputDir);
            using var firstResponse = await GetRangeAsync(uri, 0, InitialRangeSize - 1, ct);
            if (firstResponse.StatusCode == HttpStatusCode.OK)
            {
                await SaveFullResponseAndDecodeAsync(firstResponse, outputDir, progress, includeFile, ct);
                return;
            }

            if (firstResponse.StatusCode != HttpStatusCode.PartialContent)
                firstResponse.EnsureSuccessStatusCode();

            long packageLength = firstResponse.Content.Headers.ContentRange?.Length
                ?? throw new InvalidDataException("The SNG server did not report the package length for its byte range.");
            if (firstResponse.Content.Headers.ContentRange?.From != 0)
                throw new InvalidDataException("The SNG server returned an unexpected initial byte range.");
            byte[] prefix = await firstResponse.Content.ReadAsByteArrayAsync(ct);
            progress?.Report(0.01);

            SngPackageIndex index;
            int requestedPrefixSize = InitialRangeSize;
            while (true)
            {
                try
                {
                    index = ReadPackageIndex(prefix);
                    break;
                }
                catch (EndOfStreamException) when (requestedPrefixSize < MaximumIndexSize && prefix.Length < packageLength)
                {
                    requestedPrefixSize = Math.Min(requestedPrefixSize * 2, MaximumIndexSize);
                    long requestedEnd = Math.Min(requestedPrefixSize - 1L, packageLength - 1);
                    using var prefixResponse = await GetRangeAsync(uri, 0, requestedEnd, ct);
                    if (prefixResponse.StatusCode == HttpStatusCode.OK)
                    {
                        await SaveFullResponseAndDecodeAsync(prefixResponse, outputDir, progress, includeFile, ct);
                        return;
                    }
                    if (prefixResponse.StatusCode != HttpStatusCode.PartialContent)
                        prefixResponse.EnsureSuccessStatusCode();

                    long responseLength = prefixResponse.Content.Headers.ContentRange?.Length ?? packageLength;
                    if (responseLength != packageLength)
                        throw new InvalidDataException("The SNG package length changed during the ranged download.");
                    prefix = await prefixResponse.Content.ReadAsByteArrayAsync(ct);
                }
            }

            var selectedFiles = new List<SngFileEntry>();
            foreach (SngFileEntry entry in index.Files)
            {
                string safeName = Path.GetFileName(entry.FileName);
                if (string.IsNullOrWhiteSpace(safeName) || IsVideoFile(safeName))
                    continue;
                if (includeFile != null && !includeFile(safeName))
                    continue;
                if (entry.ContentLength == 0)
                    continue;
                if (entry.ContentIndex > (ulong)packageLength || entry.ContentLength > (ulong)packageLength - entry.ContentIndex)
                    throw new InvalidDataException($"SNG entry '{safeName}' points outside the package.");
                selectedFiles.Add(entry);
            }

            long bytesToFetch = prefix.Length;
            foreach (SngFileEntry entry in selectedFiles)
            {
                if (entry.ContentLength > (ulong)(long.MaxValue - bytesToFetch))
                    throw new InvalidDataException("The selected SNG files are too large to process.");
                bytesToFetch += (long)entry.ContentLength;
            }

            long bytesFetched = prefix.Length;
            foreach (SngFileEntry entry in selectedFiles)
            {
                ct.ThrowIfCancellationRequested();
                string safeName = Path.GetFileName(entry.FileName);
                long start = checked((long)entry.ContentIndex);
                long end = checked(start + (long)entry.ContentLength - 1);
                using var response = await GetRangeAsync(uri, start, end, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    await SaveFullResponseAndDecodeAsync(response, outputDir, progress, includeFile, ct);
                    return;
                }
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    response.EnsureSuccessStatusCode();

                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.From != start || contentRange.To != end || contentRange.Length != packageLength)
                    throw new InvalidDataException($"The SNG server returned an unexpected range for '{safeName}'.");

                string destination = Path.Combine(outputDir, safeName);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                byte[] buffer = new byte[64 * 1024];
                ulong entryBytesRead = 0;
                while (entryBytesRead < entry.ContentLength)
                {
                    int wanted = (int)Math.Min((ulong)buffer.Length, entry.ContentLength - entryBytesRead);
                    int read = await input.ReadAsync(buffer.AsMemory(0, wanted), ct);
                    if (read <= 0)
                        throw new EndOfStreamException($"The SNG entry '{safeName}' ended unexpectedly.");

                    for (int i = 0; i < read; i++)
                    {
                        ulong fileBytePos = entryBytesRead + (ulong)i;
                        buffer[i] ^= (byte)(index.XorMask[(int)(fileBytePos % 16)] ^ (byte)(fileBytePos & 0xFF));
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    entryBytesRead += (ulong)read;
                    bytesFetched += read;
                    if (bytesToFetch > 0)
                        progress?.Report(Math.Min(1.0, (double)bytesFetched / bytesToFetch));
                }
            }

            WriteSongIniIfMissing(outputDir, index.Metadata);
            progress?.Report(1.0);
        }

        public static Dictionary<string, string> Decode(
            string sngFilePath,
            string outputDir,
            Func<string, bool>? includeFile = null)
        {
            if (!File.Exists(sngFilePath))
                throw new FileNotFoundException($"SNG file not found: {sngFilePath}");

            Directory.CreateDirectory(outputDir);
            using var fs = new FileStream(sngFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            SngPackageIndex index = ReadPackageIndex(reader);

            const int chunkSize = 64 * 1024;
            byte[] buffer = new byte[chunkSize];
            foreach (SngFileEntry entry in index.Files)
            {
                string safeName = Path.GetFileName(entry.FileName);
                if (string.IsNullOrWhiteSpace(safeName) || IsVideoFile(safeName))
                    continue;
                if (includeFile != null && !includeFile(safeName))
                    continue;
                if (entry.ContentIndex > (ulong)fs.Length || entry.ContentLength > (ulong)fs.Length - entry.ContentIndex)
                    throw new InvalidDataException($"SNG entry '{safeName}' points outside the package.");

                string destFile = Path.Combine(outputDir, safeName);
                fs.Seek((long)entry.ContentIndex, SeekOrigin.Begin);
                using var outFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
                ulong bytesReadTotal = 0;
                while (bytesReadTotal < entry.ContentLength)
                {
                    int toRead = (int)Math.Min((ulong)buffer.Length, entry.ContentLength - bytesReadTotal);
                    int read = fs.Read(buffer, 0, toRead);
                    if (read <= 0) throw new EndOfStreamException($"The SNG entry '{safeName}' ended unexpectedly.");

                    for (int i = 0; i < read; i++)
                    {
                        ulong fileBytePos = bytesReadTotal + (ulong)i;
                        buffer[i] ^= (byte)(index.XorMask[(int)(fileBytePos % 16)] ^ (byte)(fileBytePos & 0xFF));
                    }

                    outFs.Write(buffer, 0, read);
                    bytesReadTotal += (ulong)read;
                }
            }

            WriteSongIniIfMissing(outputDir, index.Metadata);
            return index.Metadata;
        }

        private static async Task<HttpResponseMessage> GetRangeAsync(Uri uri, long start, long end, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            return await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        private static async Task<byte[]?> ReadRemoteEntryBytesAsync(
            Uri uri,
            SngPackageIndex index,
            SngFileEntry entry,
            CancellationToken ct)
        {
            if (entry.ContentIndex > (ulong)long.MaxValue || entry.ContentLength > (ulong)long.MaxValue)
                return null;

            long start = checked((long)entry.ContentIndex);
            long end = checked(start + (long)entry.ContentLength - 1);
            using var response = await GetRangeAsync(uri, start, end, ct);
            if (response.StatusCode == HttpStatusCode.OK)
                return null; // Do not read a full SNG package as a fallback.
            if (response.StatusCode != HttpStatusCode.PartialContent)
                response.EnsureSuccessStatusCode();

            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange?.From != start || contentRange.To != end ||
                (contentRange.Length.HasValue && end >= contentRange.Length.Value))
            {
                throw new InvalidDataException($"The SNG server returned an unexpected range for '{Path.GetFileName(entry.FileName)}'.");
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if ((ulong)bytes.Length != entry.ContentLength)
                throw new EndOfStreamException($"The SNG entry '{Path.GetFileName(entry.FileName)}' ended unexpectedly.");

            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= (byte)(index.XorMask[i % 16] ^ (byte)(i & 0xFF));
            return bytes;
        }

        private static async Task<SngPackageIndex?> ReadRemotePackageIndexAsync(Uri uri, CancellationToken ct)
        {
            using var firstResponse = await GetRangeAsync(uri, 0, InitialRangeSize - 1, ct);
            if (firstResponse.StatusCode == HttpStatusCode.OK)
                return null; // The server ignored Range; do not download the whole chart just to inspect it.
            if (firstResponse.StatusCode != HttpStatusCode.PartialContent)
                firstResponse.EnsureSuccessStatusCode();

            long packageLength = firstResponse.Content.Headers.ContentRange?.Length
                ?? throw new InvalidDataException("The SNG server did not report the package length for its byte range.");
            if (firstResponse.Content.Headers.ContentRange?.From != 0)
                throw new InvalidDataException("The SNG server returned an unexpected initial byte range.");

            byte[] prefix = await firstResponse.Content.ReadAsByteArrayAsync(ct);
            int requestedPrefixSize = InitialRangeSize;
            while (true)
            {
                try
                {
                    return ReadPackageIndex(prefix);
                }
                catch (EndOfStreamException) when (requestedPrefixSize < MaximumIndexSize && prefix.Length < packageLength)
                {
                    requestedPrefixSize = Math.Min(requestedPrefixSize * 2, MaximumIndexSize);
                    long requestedEnd = Math.Min(requestedPrefixSize - 1L, packageLength - 1);
                    using var prefixResponse = await GetRangeAsync(uri, 0, requestedEnd, ct);
                    if (prefixResponse.StatusCode == HttpStatusCode.OK)
                        return null;
                    if (prefixResponse.StatusCode != HttpStatusCode.PartialContent)
                        prefixResponse.EnsureSuccessStatusCode();

                    var contentRange = prefixResponse.Content.Headers.ContentRange;
                    if (contentRange?.From != 0 || contentRange.Length != packageLength)
                        throw new InvalidDataException("The SNG package length changed while reading its index.");
                    prefix = await prefixResponse.Content.ReadAsByteArrayAsync(ct);
                }
            }
        }

        private static async Task SaveFullResponseAndDecodeAsync(
            HttpResponseMessage response,
            string outputDir,
            IProgress<double>? progress,
            Func<string, bool>? includeFile,
            CancellationToken ct)
        {
            response.EnsureSuccessStatusCode();
            string tempSng = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".download.sng";
            try
            {
                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tempSng, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                byte[] buffer = new byte[128 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                    if (totalBytes > 0) progress?.Report(Math.Min(1.0, (double)copied / totalBytes));
                }
                await output.FlushAsync(ct);
                Decode(tempSng, outputDir, includeFile);
            }
            finally
            {
                try { if (File.Exists(tempSng)) File.Delete(tempSng); } catch { }
            }
            progress?.Report(1.0);
        }

        private static SngPackageIndex ReadPackageIndex(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            return ReadPackageIndex(reader);
        }

        private static SngPackageIndex ReadPackageIndex(BinaryReader reader)
        {
            byte[] identBytes = ReadRequiredBytes(reader, 6);
            string ident = Encoding.ASCII.GetString(identBytes);
            if (ident != "SNGPKG" && ident != "SNGAPP")
                throw new InvalidDataException($"Invalid SNG file identifier: '{ident}'. Expected 'SNGPKG'.");

            _ = reader.ReadUInt32(); // version
            byte[] xorMask = ReadRequiredBytes(reader, 16);
            _ = reader.ReadUInt64(); // metadata length
            ulong metadataCount = reader.ReadUInt64();
            if (metadataCount > 10000) throw new InvalidDataException("The SNG metadata table is unexpectedly large.");

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (ulong i = 0; i < metadataCount; i++)
            {
                uint keyLength = reader.ReadUInt32();
                if (keyLength > 1024 * 1024) throw new InvalidDataException("An SNG metadata key is too large.");
                string key = Encoding.UTF8.GetString(ReadRequiredBytes(reader, (int)keyLength));
                uint valueLength = reader.ReadUInt32();
                if (valueLength > 1024 * 1024) throw new InvalidDataException("An SNG metadata value is too large.");
                string value = Encoding.UTF8.GetString(ReadRequiredBytes(reader, (int)valueLength));
                metadata[key] = value;
            }

            _ = reader.ReadUInt64(); // file metadata length
            ulong fileCount = reader.ReadUInt64();
            if (fileCount > 10000) throw new InvalidDataException("The SNG file table is unexpectedly large.");

            var files = new List<SngFileEntry>((int)fileCount);
            for (ulong i = 0; i < fileCount; i++)
            {
                int nameLength = reader.ReadByte();
                string name = Encoding.UTF8.GetString(ReadRequiredBytes(reader, nameLength));
                files.Add(new SngFileEntry
                {
                    FileName = name,
                    ContentLength = reader.ReadUInt64(),
                    ContentIndex = reader.ReadUInt64()
                });
            }

            _ = reader.ReadUInt64(); // file data length
            return new SngPackageIndex { XorMask = xorMask, Metadata = metadata, Files = files };
        }

        private static byte[] ReadRequiredBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return bytes;
        }

        private static bool IsVideoFile(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteSongIniIfMissing(string outputDir, Dictionary<string, string> metadata)
        {
            string songIniPath = Path.Combine(outputDir, "song.ini");
            if (File.Exists(songIniPath) || metadata.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("[song]");
            foreach (var kvp in metadata)
                sb.AppendLine($"{kvp.Key} = {kvp.Value}");
            File.WriteAllText(songIniPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
