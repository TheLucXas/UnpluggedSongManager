using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services.Diagnostics;
using USM.Core.Services.Quest;

namespace USM.Tests
{
    /// <summary>Diagnostics checks: <c>--diagnostics</c>. All sample values are fabricated.</summary>
    internal static class DiagnosticsTests
    {
        private static int _failures;
        private static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
            if (!ok) _failures++;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "USM.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Repo root (USM.slnx) not found");
        }

        private static void Gone(string text, string name, params string[] secrets)
        {
            string r = DiagnosticRedactor.Redact(text);
            var leaked = secrets.Where(s => r.Contains(s, StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(leaked.Length == 0, name, $"leaked [{string.Join(", ", leaked)}] in: {r}");
        }

        private static void Same(string text, string name)
        {
            string r = DiagnosticRedactor.Redact(text);
            Check(r == text, name, $"changed to: {r}");
        }

        public static async Task<int> RunAsync(string[] args)
        {
            Console.WriteLine("=== Diagnostics tests ===");
            string work = Path.Combine(Root(), "build", "issue1-verify");
            string logDir = Path.Combine(work, "logs");
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
            Directory.CreateDirectory(logDir);
            DiagnosticLog.Configure(logDir);

            RedactionRules();
            MustNotMangle();
            LogBasics(logDir);
            Rotation(logDir);
            Report(logDir, work);
            Concurrency(logDir);
            await InstallerFailure(logDir, work);

            Console.WriteLine(_failures == 0 ? "\nALL DIAGNOSTICS TESTS PASSED" : $"\n{_failures} DIAGNOSTICS CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        private static void RedactionRules()
        {
            Console.WriteLine("\n[REDACTION RULES]");
            Gone("Authorization: Bearer abcDEF1234567890xyz", "Authorization header", "abcDEF1234567890xyz");
            Gone("sent header authorization=Basic Zm9vOmJhcg==", "Authorization basic", "Zm9vOmJhcg");
            Gone("retry with Bearer qwertyUIOP12345678", "Bearer value", "qwertyUIOP12345678");
            Gone("tok ghp_FAKEFAKEFAKEFAKEFAKE1234567890abcd end", "ghp_ token", "FAKEFAKEFAKEFAKEFAKE");
            Gone("tok gho_FAKEFAKEFAKEFAKEFAKE1234567890abcd end", "gho_ token", "FAKEFAKEFAKEFAKEFAKE");
            Gone("tok github_pat_11FAKEFAKE0_abcdefghijklmnopqrstuvwxyz", "github_pat_ token", "abcdefghijklmnopqrstuvwxyz");
            Gone("api_key=SuperFakeKey99 other", "key param", "SuperFakeKey99");
            Gone("accessToken: fake-tok-777 x", "token param", "fake-tok-777");
            Gone("client_secret=fakeSecret42&a=1", "secret param", "fakeSecret42");
            Gone("password: \"hunter two\"", "password param quoted", "hunter two");
            Gone("{\"password\":\"fakePw123\"}", "password param json", "fakePw123");
            Gone("X-Amz-Signature=fakeSig0123456789", "signature param", "fakeSig0123456789");
            string url = DiagnosticRedactor.Redact("GET https://example.com/dl/file.zip?name=a&token=zzTOPzz#frag99 failed");
            Check(url.Contains("https://example.com/dl/file.zip") && !url.Contains("zzTOPzz") && !url.Contains("name=a") && !url.Contains("frag99"), "URL query and fragment removed, path kept", url);
            Gone("https://fakeuser:fakepass@example.com/x", "URL userinfo", "fakeuser", "fakepass");
            Gone("connect 192.168.77.123:5555 now", "IPv4 with port", "192.168.77.123", "5555");
            Gone("peer 10.20.30.40", "IPv4", "10.20.30.40");
            Gone("addr 2001:db8:85a3::8a2e:370:7334 up", "IPv6", "2001:db8:85a3");
            Gone("addr fe80::1ff:fe23:4567:890a up", "IPv6 link-local", "fe80::1ff");
            Gone("mac AA:BB:CC:11:22:33 and aa-bb-cc-11-22-44", "MAC", "AA:BB:CC:11:22:33", "aa-bb-cc-11-22-44");
            Gone("mail someone.fake@example.org please", "email", "someone.fake@example.org");
            DiagnosticRedactor.RegisterSensitiveValue("fakeSerialXyz");
            Gone("device fakeserialxyz offline", "registered serial", "fakeserialxyz");
            Gone("device 1WMHH8A12345XY unauthorized", "serial heuristic", "1WMHH8A12345XY");
            Gone($"hello {Environment.UserName} there", "current user name", Environment.UserName);
            Gone($"host {Environment.MachineName} down", "machine name", Environment.MachineName);

            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string r1 = DiagnosticRedactor.Redact(Path.Combine(la, "USM", "tools", "quest", "apktool.jar"));
            Check(r1.StartsWith("%LOCALAPPDATA%") && !r1.Contains(Environment.UserName), "LocalAppData prefix", r1);
            string r2 = DiagnosticRedactor.Redact(Path.Combine(Path.GetTempPath(), "usm-work", "a.apk"));
            Check(r2.StartsWith("%TEMP%") && !r2.Contains(Environment.UserName), "Temp prefix", r2);
            string r3 = DiagnosticRedactor.Redact(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "x.txt"));
            Check(r3.StartsWith("%USERPROFILE%") && !r3.Contains(Environment.UserName), "UserProfile prefix", r3);
            string r4 = DiagnosticRedactor.Redact("missing Z:\\Fake Games\\Some Game\\game.exe here");
            Check(r4.Contains("<path>\\game.exe") && !r4.Contains("Fake Games") && !r4.Contains("Some Game"), "other drive path keeps file name only", r4);
            string r5 = DiagnosticRedactor.Redact("open \\\\fakenas\\share\\music\\song.ogg now");
            Check(r5.Contains("<path>\\song.ogg") && !r5.Contains("fakenas") && !r5.Contains("music"), "UNC path keeps file name only", r5);

            const string mixed = "x Authorization: Bearer aaaaBBBB1111 y ghp_FAKEFAKEFAKEFAKEFAKE1234 192.168.1.9 Z:\\a\\b.txt";
            string once = DiagnosticRedactor.Redact(mixed);
            Check(DiagnosticRedactor.Redact(once) == once, "redaction is idempotent", once);

            bool noThrow = true;
            try
            {
                DiagnosticRedactor.Redact(null);
                DiagnosticRedactor.Redact("");
                DiagnosticRedactor.Redact(new string(':', 50000));
                DiagnosticRedactor.Redact(new string('1', 50000) + "." + new string('9', 50000));
                DiagnosticRedactor.Redact("\0\u0001\uD800");
            }
            catch { noThrow = false; }
            Check(noThrow, "Redact never throws (null, empty, huge, pathological)");
        }

        private static void MustNotMangle()
        {
            Console.WriteLine("\n[MUST NOT MANGLE]");
            Same("HTTP 404", "HTTP 404");
            Same("HTTP 404 for https://github.com/LemonLoader/MelonLoader/releases/download/0.6.5.1/melon_data.zip", "GitHub URL with /download/0.6.5.1/");
            Same("MelonLoader v0.6.5.1 installed", "v0.6.5.1");
            Same("Assembly Version=1.2.3.4, Culture=neutral", "Version=1.2.3.4");
            Same("Mod version 1.5.410 ready", "1.5.410");
            Same("sha256 dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423 ok", "64-char hex hash");
            Same("md5 0123456789abcdef0123456789abcdef ok", "32-char hex hash");
            Same("DBF930B076C6B9BE08D57C449CACEFC3BDD6B71EBD59B3066FC0E1F5B14F9423", "64-char uppercase hex hash");
            Same("HRESULT 0x80070005 access denied", "0x80070005");
            Same("System.Net.Http.HttpRequestException: boom", "exception type name");
            Same("System.Collections.Generic.KeyNotFoundException: no entry", "KeyNotFoundException type and message");
            Same("push to /sdcard/ModData/com.example.fake/Songs/abc ok", "/sdcard path");
            Same("12:30:45 started", "time of day");
            Same("Foo::Bar called", "C++ style scope");
        }

        private static string ReadLogFiles(string dir) =>
            string.Join("\n", new[] { "usm.1.log", "usm.log" }.Select(n => Path.Combine(dir, n)).Where(File.Exists).Select(File.ReadAllText));

        private static void LogBasics(string dir)
        {
            Console.WriteLine("\n[LOG]");
            DiagnosticLog.Info("Test op", "info line 192.168.5.5 token=leakTokenAAA");
            DiagnosticLog.Warn("Test op", "warn line");
            var ex = new InvalidOperationException("boom mail me@example.org");
            DiagnosticLog.Failure("Failing op", "it broke ghp_FAKEFAKEFAKEFAKEFAKE1234", ex, "Kind=NotFound", "Url=https://example.com/a.zip?sig=abc", "Output=line1\nline2 secret=zzz");
            string text = ReadLogFiles(dir);
            Check(Regex.IsMatch(text, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d INFO \[Test op\] info line", RegexOptions.Multiline), "line format 'yyyy-MM-dd HH:mm:ss LEVEL [operation] message'");
            Check(text.Contains(" WARN [Test op] warn line") && text.Contains(" ERROR [Failing op] it broke"), "WARN and ERROR levels");
            Check(!text.Contains("192.168.5.5") && !text.Contains("leakTokenAAA") && !text.Contains("FAKEFAKEFAKE") && !text.Contains("me@example.org") && !text.Contains("sig=abc") && !text.Contains("zzz"),
                "nothing sensitive reached the log file");

            var f = DiagnosticLog.LastFailure;
            Check(f != null && f.Operation == "Failing op" && f.ExceptionType == "System.InvalidOperationException" && f.StackTrace != null && f.Details.Count == 3, "LastFailure in memory");
            DiagnosticLog.Configure(dir); // simulates a restart
            var g = DiagnosticLog.LastFailure;
            Check(g != null && g.Operation == "Failing op" && g.ExceptionType == "System.InvalidOperationException" && g.Details.Any(d => d.StartsWith("Kind=NotFound")) && g.Details.Any(d => d.Contains("line2")),
                "LastFailure restored from the log after restart", g?.ToString() ?? "null");
            var recent = DiagnosticLog.ReadRecentLines(100000);
            Check(recent.Count >= 3 && DiagnosticLog.ReadRecentLines(50).Sum(l => l.Length + 1) <= 50, "ReadRecentLines honours maxChars");
        }

        private static void Rotation(string dir)
        {
            Console.WriteLine("\n[ROTATION]");
            string filler = new string('x', 900);
            for (int i = 0; i < 3500; i++) DiagnosticLog.Info("Rotate", $"{i} {filler}");
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Check(files.SequenceEqual(new[] { "usm.1.log", "usm.log" }), "only usm.log and usm.1.log are kept", string.Join(",", files));
            long max = files.Max(n => new FileInfo(Path.Combine(dir, n!)).Length);
            Check(max <= 1024 * 1024 + 2000, "each file stays near or under 1 MB", max.ToString());
            Check(new FileInfo(Path.Combine(dir, "usm.1.log")).Length > 900_000, "usm.1.log holds the rotated content");
        }

        private static void Report(string dir, string work)
        {
            Console.WriteLine("\n[BUG REPORT]");
            DiagnosticLog.Configure(dir);
            DiagnosticLog.Info("Report op", "sample 172.16.4.4 and fakemail@example.org and Z:\\Secret Stuff\\file.bin");
            string toolsDir = Path.Combine(work, "diag-empty-tools");
            Directory.CreateDirectory(toolsDir);

            var small = BugReportBuilder.Build(3000, toolsDir);
            Check(small.Text.Length <= 3000 && small.WasTrimmed, "cap 3000 respected and flagged trimmed", small.Text.Length.ToString());
            var def = BugReportBuilder.Build(20000, toolsDir);
            Check(def.Text.Length <= 20000 && def.WasTrimmed, "default cap 20000 respected (log is larger)", def.Text.Length.ToString());
            var tiny = BugReportBuilder.Build(120, toolsDir);
            Check(tiny.Text.Length <= 120 && tiny.WasTrimmed, "hard truncation never exceeds cap", tiny.Text.Length.ToString());
            var zero = BugReportBuilder.Build(0, toolsDir);
            Check(zero.Text.Length == 0, "cap 0 yields empty text");

            string t = def.Text;
            Check(t.Contains("USM version") && t.Contains("Windows") && t.Contains(".NET runtime") && t.Contains("Architecture") && t.Contains("UI language"), "environment section");
            Check(t.Contains("Quest tools") && t.Contains("apktool.jar: missing") && !t.Contains(toolsDir) && !t.Contains("diag-empty-tools"), "Quest tools status per component, no path");
            Check(!t.Contains("172.16.4.4") && !t.Contains("fakemail@") && !t.Contains("Secret Stuff") && !t.Contains(Environment.UserName), "nothing sensitive in the report");

            DiagnosticLog.Configure(Path.Combine(work, "logs-empty"));
            if (Directory.Exists(Path.Combine(work, "logs-empty"))) Directory.Delete(Path.Combine(work, "logs-empty"), true);
            var none = BugReportBuilder.Build(20000, toolsDir);
            Check(none.Text.Contains("None recorded") && !none.WasTrimmed, "clear 'none recorded' without failures; not trimmed");
            DiagnosticLog.Configure(dir);
        }

        private static void Concurrency(string dir)
        {
            Console.WriteLine("\n[CONCURRENCY]");
            string cdir = Path.Combine(Path.GetDirectoryName(dir)!, "logs-concurrent");
            if (Directory.Exists(cdir)) Directory.Delete(cdir, true);
            DiagnosticLog.Configure(cdir);
            var errors = new List<Exception>();
            var threads = Enumerable.Range(0, 8).Select(n => new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 150; i++)
                    {
                        DiagnosticLog.Info("Conc", $"t{n} line {i}");
                        if (i % 50 == 0) DiagnosticLog.Failure("Conc", $"t{n} fail {i}", new Exception("x"), "Kind=Test");
                        _ = DiagnosticLog.LastFailure;
                        _ = DiagnosticRedactor.Redact("addr 10.1.1.1 token=abc");
                    }
                }
                catch (Exception e) { lock (errors) errors.Add(e); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            string all = ReadLogFiles(cdir);
            int info = Regex.Matches(all, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d INFO \[Conc\] t\d+ line \d+$", RegexOptions.Multiline).Count;
            Check(errors.Count == 0 && info == 8 * 150, "8 threads x 150 lines: no exception, no line lost or mixed", $"errors={errors.Count} info={info}");
            DiagnosticLog.Configure(dir);
        }

        private static async Task InstallerFailure(string dir, string work)
        {
            Console.WriteLine("\n[SIMULATED TOOLS 404]");
            string ldir = Path.Combine(work, "logs-404");
            if (Directory.Exists(ldir)) Directory.Delete(ldir, true);
            DiagnosticLog.Configure(ldir);

            string cache = Path.Combine(work, "diag-404-tools");
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
            const string url = "https://github.com/LemonLoader/MelonLoader/releases/download/0.6.5.1/fake_component.zip";
            var src = new QuestToolsSource("fake_component.zip", url, 10, new string('a', 64),
                new[] { new QuestToolsTarget("fake_component.zip", null, 10, new string('a', 64)) });
            var installer = new QuestToolsInstaller(new FakeHandler(), new[] { src }, TimeSpan.Zero);
            var result = await installer.InstallAsync(cache);
            Check(!result.Success && result.Kind == QuestToolsFailureKind.NotFound && result.HttpStatus == 404, "installer reports NotFound 404");

            QuestService.ReportToolsInstallFailure(result);
            var report = BugReportBuilder.Build(20000, cache).Text;
            Check(report.Contains("Quest tools download"), "report names the operation");
            Check(report.Contains("fake_component.zip"), "report names the component");
            Check(report.Contains(url), "report includes the URL");
            Check(report.Contains("HttpStatus=404"), "report includes the HTTP status");
            Check(report.Contains("Kind=NotFound"), "report includes the failure kind");
            Check(!report.Contains(cache), "report contains no local path");
            DiagnosticLog.Configure(dir);
        }
    }
}
