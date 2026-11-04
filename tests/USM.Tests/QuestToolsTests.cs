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
using USM.Core.Services.Quest;

namespace USM.Tests
{
    /// <summary>Quest tools installer / LemonLoader bridge checks: <c>--quest-tools [--online]</c>.</summary>
    internal static class QuestToolsTests
    {
        private static int _failures;
        private static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
            if (!ok) _failures++;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
            public int Calls;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult(_fn(request));
            }
        }

        private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
        private static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        private static string NewDir(string w)
        {
            var d = Path.Combine(w, "offline-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            return d;
        }
        private static bool Clean(string d) => !Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any();

        public static async Task<int> RunAsync(string[] args)
        {
            Console.WriteLine("=== Quest tools tests ===");
            string root = FindRoot();
            string work = Path.Combine(root, "build", "issue1-verify");
            Directory.CreateDirectory(work);

            // Fake manifest: one plain file and one zip providing two entries.
            byte[] plain = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
            byte[] e1 = Enumerable.Range(0, 3000).Select(i => (byte)(i * 3)).ToArray();
            byte[] e2 = Enumerable.Range(0, 2000).Select(i => (byte)(i * 5 + 1)).ToArray();
            byte[] zip;
            using (var ms = new MemoryStream())
            {
                using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    foreach (var (n, d) in new[] { ("a.dll", e1), ("b.dll", e2), ("ignored.txt", new byte[] { 1, 2, 3 }) })
                    {
                        using var s = za.CreateEntry(n).Open();
                        s.Write(d);
                    }
                }
                zip = ms.ToArray();
            }
            var sources = new[]
            {
                new QuestToolsSource("plain.bin", "https://fake.test/plain.bin", plain.Length, Sha(plain),
                    new[] { new QuestToolsTarget("plain.bin", null, plain.Length, Sha(plain)) }),
                new QuestToolsSource("pack.zip", "https://fake.test/pack.zip", zip.Length, Sha(zip),
                    new[]
                    {
                        new QuestToolsTarget("sub/a.dll", "a.dll", e1.Length, Sha(e1)),
                        new QuestToolsTarget("sub/b.dll", "b.dll", e2.Length, Sha(e2)),
                    }),
            };
            HttpResponseMessage Serve(HttpRequestMessage r) =>
                r.RequestUri!.AbsolutePath.EndsWith("plain.bin") ? Ok(plain) : Ok(zip);
            var noDelay = TimeSpan.Zero;

            Console.WriteLine("\n[OFFLINE] 404 -> NotFound");
            {
                var d = NewDir(work);
                var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
                var r = await new QuestToolsInstaller(h, sources, noDelay).InstallAsync(d);
                Check(!r.Success && r.Kind == QuestToolsFailureKind.NotFound && r.HttpStatus == 404, "NotFound, no success", $"{r.Kind}");
                Check(r.Message.Length > 20 && r.Component == "plain.bin", "message and component set", r.Message);
                Check(Clean(d), "no file left behind");
                Check(h.Calls == 1, "404 is not retried", h.Calls.ToString());
                Directory.Delete(d, true);
            }

            Console.WriteLine("\n[OFFLINE] bad hash -> Integrity");
            {
                var d = NewDir(work);
                var bad = (byte[])plain.Clone(); bad[10] ^= 0xFF;
                var h = new FakeHandler(_ => Ok(bad));
                var r = await new QuestToolsInstaller(h, sources, noDelay).InstallAsync(d);
                Check(!r.Success && r.Kind == QuestToolsFailureKind.Integrity, "Integrity, no success", $"{r.Kind}");
                Check(Clean(d), "nothing moved into place, no temps");
                Directory.Delete(d, true);
            }

            Console.WriteLine("\n[OFFLINE] wrong size -> Integrity");
            {
                var d = NewDir(work);
                var h = new FakeHandler(_ => Ok(plain.Take(100).ToArray()));
                var r = await new QuestToolsInstaller(h, sources, noDelay).InstallAsync(d);
                Check(!r.Success && r.Kind == QuestToolsFailureKind.Integrity && Clean(d), "truncated body rejected");
                Directory.Delete(d, true);
            }

            Console.WriteLine("\n[OFFLINE] network exception -> Network (three attempts)");
            {
                var d = NewDir(work);
                var h = new FakeHandler(_ => throw new HttpRequestException("boom"));
                var r = await new QuestToolsInstaller(h, sources, noDelay).InstallAsync(d);
                Check(!r.Success && r.Kind == QuestToolsFailureKind.Network, "Network, no success", $"{r.Kind}");
                Check(h.Calls == 3, "tried three times", h.Calls.ToString());
                Check(LemonPatcherBridge.IsNetworkFailure("System.Net.Sockets.SocketException (10013): denied"), "patcher socket error counts as network");
                Check(LemonPatcherBridge.IsNetworkFailure("System.Net.Http.HttpRequestException: failed (github.com:443)"), "patcher HTTP error counts as network");
                Check(!LemonPatcherBridge.IsNetworkFailure("System.IO.IOException: bad apk"), "other patcher errors are not retried");
                Check(Clean(d), "no file left behind");
                Directory.Delete(d, true);
            }

            Console.WriteLine("\n[OFFLINE] 503 -> retry succeeds; persistent 503 -> Http");
            {
                var d = NewDir(work);
                int n = 0;
                var h = new FakeHandler(r => Interlocked.Increment(ref n) == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Serve(r));
                var r = await new QuestToolsInstaller(h, sources, noDelay).InstallAsync(d);
                Check(r.Success, "recovered after one 503", r.Message);
                Directory.Delete(d, true);

                d = NewDir(work);
                var h2 = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var r2 = await new QuestToolsInstaller(h2, sources, noDelay).InstallAsync(d);
                Check(!r2.Success && r2.Kind == QuestToolsFailureKind.Http && r2.HttpStatus == 503, "Http 503", $"{r2.Kind}");
                Directory.Delete(d, true);
            }

            Console.WriteLine("\n[OFFLINE] install, selective extraction, skip valid");
            {
                var d = NewDir(work);
                var inst = new QuestToolsInstaller(new FakeHandler(Serve), sources, noDelay);
                Check(!inst.IsInstalled(d), "not installed initially");
                var r = await inst.InstallAsync(d);
                Check(r.Success, "install succeeds", r.Message);
                Check(inst.IsInstalled(d) && await inst.VerifyInstalledAsync(d), "IsInstalled + VerifyInstalled");
                Check(!File.Exists(Path.Combine(d, "sub", "ignored.txt")), "only named entries extracted");
                Check(!Directory.EnumerateFiles(d, ".usm-tmp-*", SearchOption.AllDirectories).Any(), "no temp files");

                var never = new FakeHandler(_ => throw new InvalidOperationException("HTTP must not be used"));
                var r2 = await new QuestToolsInstaller(never, sources, noDelay).InstallAsync(d);
                Check(r2.Success && never.Calls == 0, "valid files skipped without HTTP", $"calls={never.Calls}");

                // Same-size corruption: fast check passes, full verify fails, install repairs only that source.
                string a = Path.Combine(d, "sub", "a.dll");
                var bytes = File.ReadAllBytes(a); bytes[0] ^= 0xFF; File.WriteAllBytes(a, bytes);
                Check(inst.IsInstalled(d) && !await inst.VerifyInstalledAsync(d), "same-size corruption caught only by hash");
                var h3 = new FakeHandler(Serve);
                var r3 = await new QuestToolsInstaller(h3, sources, noDelay).InstallAsync(d);
                Check(r3.Success && h3.Calls == 1 && await inst.VerifyInstalledAsync(d), "repaired by re-downloading only the zip", $"calls={h3.Calls}");
                Directory.Delete(d, true);
            }

            if (args.Contains("--online"))
                await RunOnlineAsync(work);

            Console.WriteLine(_failures == 0 ? "\nQUEST TOOLS TESTS: ALL PASSED" : $"\nQUEST TOOLS TESTS: {_failures} FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static async Task RunOnlineAsync(string work)
        {
            Console.WriteLine("\n[ONLINE] real download from upstream into build/issue1-verify/cache");
            string cache = Path.Combine(work, "cache");
            var inst = new QuestToolsInstaller();
            int last = -1;
            var progress = new Progress<(double Progress, string Status)>(p =>
            {
                int bucket = (int)(p.Progress / 20);
                if (bucket != last) { last = bucket; Console.WriteLine($"    {p.Progress:F0}% {p.Status}"); }
            });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await inst.InstallAsync(cache, progress);
            Console.WriteLine($"    result: success={r.Success} kind={r.Kind} component={r.Component} http={r.HttpStatus} ({sw.Elapsed.TotalSeconds:F0}s)");
            Check(r.Success, "InstallAsync succeeded", r.Message + " | " + r.Detail);
            Check(inst.IsInstalled(cache), "IsInstalled (fast check)");
            Check(await inst.VerifyInstalledAsync(cache), "VerifyInstalledAsync (SHA-256)");
            Check(!Directory.EnumerateFiles(cache, ".usm-tmp-*", SearchOption.AllDirectories).Any(), "no temp files left");
            var again = await inst.InstallAsync(cache);
            Check(again.Success, "second install is a no-op success");

            Console.WriteLine("\n[ONLINE] LemonPatcherBridge against a dummy non-APK file");
            string dummy = Path.Combine(work, "dummy.apk");
            File.WriteAllText(dummy, "this is not an apk");
            string outDir = Path.Combine(work, "lemon_out"), tmpDir = Path.Combine(work, "lemon_temp");
            foreach (var stale in new[] { outDir, tmpDir })
                try { if (Directory.Exists(stale)) Directory.Delete(stale, true); } catch { }
            var oldOut = Console.Out;
            var sb = new StringWriter();
            int code;
            Console.SetOut(sb);
            try
            {
                code = LemonPatcherBridge.Run(new[] { dummy, outDir, tmpDir, Path.Combine(cache, "melon_data.zip"), "com.test.dummy", Path.Combine(cache, "lemon_patcher") });
            }
            finally { Console.SetOut(oldOut); }
            string output = sb.ToString();
            Console.WriteLine($"    exit code {code}; output:");
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(15)) Console.WriteLine("    | " + line.TrimEnd());
            string[] bindingErrors = { "MissingMember", "MissingMethod", "TypeLoad", "FileNotFound", "Could not load", "not found in" };
            Check(output.Contains("Starting APK patch"), "types, ctor and Run bound (reached Patcher.Run)");
            Check(!bindingErrors.Any(output.Contains), "no missing type/member/assembly errors");
            Check(code != 0, "dummy APK is not reported as success", $"code={code}");
        }

        private static string FindRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "USM.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Repo root (USM.slnx) not found");
        }
    }
}
