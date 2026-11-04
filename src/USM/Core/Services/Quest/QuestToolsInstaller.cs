using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services.Localization;

namespace USM.Core.Services.Quest
{
    public enum QuestToolsFailureKind
    {
        None,
        /// <summary>HTTP 404/410: the pinned file no longer exists upstream.</summary>
        NotFound,
        /// <summary>Any other unsuccessful HTTP status.</summary>
        Http,
        /// <summary>Connection, DNS, TLS or stalled transfer.</summary>
        Network,
        /// <summary>Downloaded bytes do not match the pinned size/SHA-256.</summary>
        Integrity,
        /// <summary>Local IO or permission problem.</summary>
        Disk
    }

    /// <summary>One file that must exist in the cache. ZipEntry is null for files stored as-is.</summary>
    public sealed record QuestToolsTarget(string RelativePath, string? ZipEntry, long Size, string Sha256);

    /// <summary>One download (a file or a zip) and the cache files it provides.</summary>
    public sealed record QuestToolsSource(string Name, string Url, long Size, string Sha256, IReadOnlyList<QuestToolsTarget> Targets);

    public sealed record QuestToolsInstallResult(
        bool Success,
        QuestToolsFailureKind Kind,
        string? Component,
        string? Url,
        int? HttpStatus,
        string? Detail,
        string Message);

    /// <summary>
    /// Downloads and verifies the third-party tools needed to patch the Quest APK. The pinned
    /// manifest below (URL, size, SHA-256) is the single source of truth.
    /// </summary>
    public sealed class QuestToolsInstaller
    {
        public const string LemonCoreRelativePath = "lemon_patcher/MelonLoader.Installer.Core.dll";

        private const string LemonBase = "https://github.com/LemonLoader/MelonLoader/releases/download/0.6.5.1/";

        public static IReadOnlyList<QuestToolsSource> DefaultSources { get; } = new[]
        {
            new QuestToolsSource("apktool.jar",
                "https://github.com/iBotPeaches/Apktool/releases/download/v3.0.3/apktool_3.0.3.jar",
                15478013, "dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423",
                new[] { new QuestToolsTarget("apktool.jar", null, 15478013, "dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423") }),
            new QuestToolsSource("uber-apk-signer.jar",
                "https://github.com/patrickfav/uber-apk-signer/releases/download/v1.3.0/uber-apk-signer-1.3.0.jar",
                3208194, "e1299fd6fcf4da527dd53735b56127e8ea922a321128123b9c32d619bba1d835",
                new[] { new QuestToolsTarget("uber-apk-signer.jar", null, 3208194, "e1299fd6fcf4da527dd53735b56127e8ea922a321128123b9c32d619bba1d835") }),
            new QuestToolsSource("melon_data.zip",
                LemonBase + "melon_data.zip",
                32815796, "1c8bab9b0bf7a462f5efe03e8dc253f0f520357915641669d0910f6a9800f3a6",
                new[] { new QuestToolsTarget("melon_data.zip", null, 32815796, "1c8bab9b0bf7a462f5efe03e8dc253f0f520357915641669d0910f6a9800f3a6") }),
            new QuestToolsSource("lemoninstaller-win-x64.zip",
                LemonBase + "lemoninstaller-win-x64.zip",
                44520014, "dcca07c27f57191bcfbdb2f7aa1c7e0ba03e2b7a23f027082c1ac42550a413d2",
                new[]
                {
                    new QuestToolsTarget(LemonCoreRelativePath, "MelonLoader.Installer.Core.dll", 70144, "0642db4b7e77cd85cbeb77999bebaeafeb2999fb53c3cd781df3c1c5c77cc3de"),
                    new QuestToolsTarget("lemon_patcher/AssetRipper.Primitives.dll", "AssetRipper.Primitives.dll", 25088, "858e66e33f25ad9e91495b3771734bb0dfe1efd3259cf61bb7c46996c55c66fd"),
                    new QuestToolsTarget("lemon_patcher/AssetsTools.NET.dll", "AssetsTools.NET.dll", 197120, "d5e04b6196e43a4e5ce32803a27f0c1a75ddea2e3066c55947b5b1df47066e96"),
                    new QuestToolsTarget("lemon_patcher/BouncyCastle.Crypto.dll", "BouncyCastle.Crypto.dll", 3318504, "f42b8609854d80d7f81f276340504aa5e82bbe4d73d05080fef1fcca2444b4d5"),
                }),
        };

        private static readonly Lazy<HttpClient> SharedClient = new(() =>
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(30) })
            { Timeout = Timeout.InfiniteTimeSpan });

        private const int MaxAttempts = 3;
        private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

        // Declared after SharedClient: static initializers run in textual order.
        public static QuestToolsInstaller Default { get; } = new QuestToolsInstaller();

        private readonly HttpClient _http;
        private readonly IReadOnlyList<QuestToolsSource> _sources;
        private readonly TimeSpan _retryDelay;

        public QuestToolsInstaller(HttpMessageHandler? handler = null, IReadOnlyList<QuestToolsSource>? sources = null, TimeSpan? retryDelay = null)
        {
            _http = handler != null
                ? new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
                : SharedClient.Value;
            _sources = sources ?? DefaultSources;
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
        }

        private static string FullPath(string dir, string rel) => Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));

        private IEnumerable<QuestToolsTarget> AllTargets => _sources.SelectMany(s => s.Targets);

        /// <summary>Fast check (existence + expected size, no hashing). Safe for property getters.</summary>
        public bool IsInstalled(string dir)
        {
            try
            {
                foreach (var t in AllTargets)
                {
                    var fi = new FileInfo(FullPath(dir, t.RelativePath));
                    if (!fi.Exists || fi.Length != t.Size) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Per download component: present when every target exists with the expected size (no hashing, no paths).</summary>
        public IReadOnlyList<(string Component, bool Present)> GetComponentStatus(string dir)
        {
            var list = new List<(string, bool)>();
            foreach (var src in _sources)
            {
                bool ok;
                try { ok = src.Targets.All(t => { var fi = new FileInfo(FullPath(dir, t.RelativePath)); return fi.Exists && fi.Length == t.Size; }); }
                catch { ok = false; }
                list.Add((src.Name, ok));
            }
            return list;
        }

        /// <summary>Full SHA-256 verification of every installed file.</summary>
        public async Task<bool> VerifyInstalledAsync(string dir, CancellationToken ct = default)
        {
            foreach (var t in AllTargets)
                if (!await IsTargetValidAsync(dir, t, ct).ConfigureAwait(false)) return false;
            return true;
        }

        private static async Task<bool> IsTargetValidAsync(string dir, QuestToolsTarget t, CancellationToken ct)
        {
            try
            {
                string path = FullPath(dir, t.RelativePath);
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != t.Size) return false;
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                return string.Equals(Convert.ToHexString(hash), t.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private sealed class InstallException : Exception
        {
            public QuestToolsFailureKind Kind { get; }
            public string Component { get; }
            public string Url { get; }
            public int? HttpStatus { get; }
            public bool Transient { get; }

            public InstallException(QuestToolsFailureKind kind, QuestToolsSource source, string detail, int? status = null, bool transient = false, Exception? inner = null)
                : base(detail, inner)
            {
                Kind = kind; Component = source.Name; Url = source.Url; HttpStatus = status; Transient = transient;
            }
        }

        public async Task<QuestToolsInstallResult> InstallAsync(string dir, IProgress<(double Progress, string Status)>? progress = null, CancellationToken ct = default)
        {
            var temps = new List<string>();
            QuestToolsSource? current = null;
            try
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail(new InstallException(QuestToolsFailureKind.Disk, _sources[0], ex.Message, inner: ex), dir);
                }

                // Work out what is missing (full hash check, off the caller's thread).
                var pending = new List<QuestToolsSource>();
                foreach (var s in _sources)
                {
                    bool valid = true;
                    foreach (var t in s.Targets)
                        if (!await IsTargetValidAsync(dir, t, ct).ConfigureAwait(false)) { valid = false; break; }
                    if (!valid) pending.Add(s);
                }

                long total = pending.Sum(s => s.Size);
                long done = 0;
                int lastPct = -1;
                void Report(string name)
                {
                    if (progress == null) return;
                    double pct = total > 0 ? Math.Min(99.0, done * 100.0 / total) : 0;
                    if ((int)pct == lastPct) return;
                    lastPct = (int)pct;
                    progress.Report((pct, LocalizationService.T("QuestTools_Downloading", name, pct)));
                }

                foreach (var source in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    current = source;
                    string tmpDownload = TempPath(dir, temps);
                    long attemptStart = done;
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await DownloadAsync(source, tmpDownload, n => { done += n; Report(source.Name); }, ct).ConfigureAwait(false);
                            break;
                        }
                        catch (InstallException e) when (e.Transient && attempt < MaxAttempts)
                        {
                            // Also gives the user time to accept a firewall prompt.
                            done = attemptStart;
                            await Task.Delay(_retryDelay * attempt, ct).ConfigureAwait(false);
                        }
                    }

                    progress?.Report((Math.Min(99.0, total > 0 ? done * 100.0 / total : 0), LocalizationService.T("QuestTools_Extracting", source.Name)));
                    await PlaceAsync(source, tmpDownload, dir, temps, ct).ConfigureAwait(false);
                }

                // Never report success unless everything verifies.
                if (!await VerifyInstalledAsync(dir, ct).ConfigureAwait(false))
                {
                    var any = _sources[0];
                    return Fail(new InstallException(QuestToolsFailureKind.Integrity, any, "Post-install verification failed."), dir);
                }

                progress?.Report((100, LocalizationService.T("QuestTools_Ready")));
                return new QuestToolsInstallResult(true, QuestToolsFailureKind.None, null, null, null, null, LocalizationService.T("QuestTools_Ready"));
            }
            catch (InstallException e)
            {
                return Fail(e, dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(new InstallException(QuestToolsFailureKind.Disk, current ?? _sources[0], ex.Message, inner: ex), dir);
            }
            finally
            {
                foreach (var t in temps)
                    try { if (File.Exists(t)) File.Delete(t); } catch { }
            }
        }

        private static string TempPath(string folder, List<string> temps)
        {
            Directory.CreateDirectory(folder);
            string p = Path.Combine(folder, ".usm-tmp-" + Guid.NewGuid().ToString("N"));
            temps.Add(p);
            return p;
        }

        private static QuestToolsInstallResult Fail(InstallException e, string dir)
        {
            string msg = e.Kind switch
            {
                QuestToolsFailureKind.NotFound => LocalizationService.T("QuestTools_ErrNotFound", e.Component),
                QuestToolsFailureKind.Http => LocalizationService.T("QuestTools_ErrHttp", e.Component, e.HttpStatus?.ToString() ?? "?"),
                QuestToolsFailureKind.Network => LocalizationService.T("QuestTools_ErrNetwork", e.Component),
                QuestToolsFailureKind.Integrity => LocalizationService.T("QuestTools_ErrIntegrity", e.Component),
                _ => LocalizationService.T("QuestTools_ErrDisk", e.Component, dir),
            };
            return new QuestToolsInstallResult(false, e.Kind, e.Component, e.Url, e.HttpStatus, e.Message, msg);
        }

        private async Task DownloadAsync(QuestToolsSource source, string tmpFile, Action<long> onBytes, CancellationToken ct)
        {
            long attemptBytes = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                request.Headers.TryAddWithoutValidation("User-Agent", "USM-SongManager/2.0");

                HttpResponseMessage response;
                try
                {
                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    headerCts.CancelAfter(HeaderTimeout);
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new InstallException(QuestToolsFailureKind.Network, source, "Timed out waiting for the server.", transient: true);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException)
                {
                    throw new InstallException(QuestToolsFailureKind.Network, source, ex.Message, transient: true, inner: ex);
                }

                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (status == 404 || status == 410)
                        throw new InstallException(QuestToolsFailureKind.NotFound, source, $"HTTP {status} for {source.Url}", status);
                    if (!response.IsSuccessStatusCode)
                        throw new InstallException(QuestToolsFailureKind.Http, source, $"HTTP {status} for {source.Url}", status,
                            transient: status >= 500 || status == 408 || status == 429);

                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    FileStream file;
                    try { file = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InstallException(QuestToolsFailureKind.Disk, source, ex.Message, inner: ex);
                    }

                    await using (file)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        byte[] buffer = new byte[81920];
                        Stream net;
                        try { net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is HttpRequestException or IOException)
                        {
                            throw new InstallException(QuestToolsFailureKind.Network, source, ex.Message, transient: true, inner: ex);
                        }

                        await using (net)
                        {
                            while (true)
                            {
                                int read;
                                try
                                {
                                    idle.CancelAfter(IdleTimeout);
                                    read = await net.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    throw new InstallException(QuestToolsFailureKind.Network, source, "Transfer stalled.", transient: true);
                                }
                                catch (Exception ex) when (ex is HttpRequestException or IOException)
                                {
                                    throw new InstallException(QuestToolsFailureKind.Network, source, ex.Message, transient: true, inner: ex);
                                }
                                if (read == 0) break;

                                try { await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    throw new InstallException(QuestToolsFailureKind.Disk, source, ex.Message, inner: ex);
                                }
                                hasher.AppendData(buffer, 0, read);
                                attemptBytes += read;
                                onBytes(read);
                            }
                        }
                    }

                    string hash = Convert.ToHexString(hasher.GetHashAndReset());
                    if (attemptBytes != source.Size || !string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InstallException(QuestToolsFailureKind.Integrity, source,
                            $"Expected {source.Size} bytes / {source.Sha256}, got {attemptBytes} bytes / {hash}.");
                }
            }
            catch (InstallException) { throw; }
        }

        /// <summary>Moves the verified download (or its verified entries) into place.</summary>
        private static async Task PlaceAsync(QuestToolsSource source, string tmpDownload, string dir, List<string> temps, CancellationToken ct)
        {
            var staged = new List<(string Tmp, string Final)>();

            if (source.Targets.All(t => t.ZipEntry == null))
            {
                // Plain file: the download itself was verified (size + SHA-256).
                var t = source.Targets.Single();
                string final = FullPath(dir, t.RelativePath);
                staged.Add((tmpDownload, final));
            }
            else
            {
                try
                {
                    using var zip = ZipFile.OpenRead(tmpDownload);
                    foreach (var t in source.Targets)
                    {
                        var entry = zip.Entries.FirstOrDefault(e => e.FullName == t.ZipEntry)
                            ?? throw new InstallException(QuestToolsFailureKind.Integrity, source, $"Entry '{t.ZipEntry}' not found in {source.Name}.");
                        string final = FullPath(dir, t.RelativePath);
                        string tmp = TempPath(Path.GetDirectoryName(final)!, temps);
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long written = 0;
                        await using (var input = entry.Open())
                        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            byte[] buffer = new byte[81920];
                            int n;
                            while ((n = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                                hasher.AppendData(buffer, 0, n);
                                written += n;
                            }
                        }
                        string hash = Convert.ToHexString(hasher.GetHashAndReset());
                        if (written != t.Size || !string.Equals(hash, t.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InstallException(QuestToolsFailureKind.Integrity, source, $"{t.ZipEntry}: expected {t.Size} bytes / {t.Sha256}, got {written} bytes / {hash}.");
                        staged.Add((tmp, final));
                    }
                }
                catch (InvalidDataException ex)
                {
                    throw new InstallException(QuestToolsFailureKind.Integrity, source, "Corrupt archive: " + ex.Message, inner: ex);
                }
            }

            // Everything verified: move into place (same volume, replaces atomically).
            foreach (var (tmp, final) in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(tmp, final, overwrite: true);
            }
        }
    }
}
