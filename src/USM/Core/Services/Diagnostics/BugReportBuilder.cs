using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using USM.Core.Services.Localization;
using USM.Core.Services.Quest;

namespace USM.Core.Services.Diagnostics
{
    public sealed record BugReportResult(string Text, bool WasTrimmed);

    /// <summary>
    /// Builds a redacted, size-capped plain-text (Markdown) report that is safe to paste into a
    /// GitHub issue. Contains no game path, song list, device list or file dumps.
    /// </summary>
    public static class BugReportBuilder
    {
        public const int DefaultMaxChars = 20000;
        private const string Marker = "\n[report truncated]";
        private const int MaxStackInReport = 3000;

        /// <param name="questToolsDir">Folder to check for Quest tools; null uses the real cache folder.</param>
        public static BugReportResult Build(int maxChars = DefaultMaxChars, string? questToolsDir = null)
        {
            try
            {
                return BuildCore(Math.Max(0, maxChars), questToolsDir);
            }
            catch
            {
                string t = "USM bug report could not be generated.";
                return new BugReportResult(t.Length <= maxChars ? t : t.Substring(0, Math.Max(0, maxChars)), true);
            }
        }

        private static BugReportResult BuildCore(int maxChars, string? questToolsDir)
        {
            var head = new StringBuilder();
            head.AppendLine("## USM bug report");
            head.AppendLine();
            head.AppendLine("- USM version: v" + Safe(AppVersion));
            head.AppendLine("- Windows: " + Safe(WindowsVersion));
            head.AppendLine("- .NET runtime: " + Safe(() => RuntimeInformation.FrameworkDescription));
            head.AppendLine("- Architecture: " + Safe(() => RuntimeInformation.OSArchitecture.ToString()));
            head.AppendLine("- UI language: " + Safe(() => LocalizationService.Instance.CurrentLanguage));
            head.AppendLine();
            head.AppendLine("### Last failed operation");
            head.AppendLine();
            AppendFailure(head, DiagnosticLog.LastFailure);
            head.AppendLine();
            head.AppendLine("### Quest tools");
            head.AppendLine();
            AppendQuestTools(head, questToolsDir);
            head.AppendLine();
            head.AppendLine("### Recent log");
            head.AppendLine();
            head.AppendLine("```");

            const string footer = "```\n";
            string header = head.ToString();

            int room = maxChars - header.Length - footer.Length;
            var lines = DiagnosticLog.ReadRecentLines(Math.Max(0, room));
            bool trimmed = false;
            // ReadRecentLines already kept only the newest lines that fit; detect whether older ones existed.
            var everything = DiagnosticLog.ReadRecentLines(int.MaxValue);
            if (everything.Count > lines.Count) trimmed = true;

            string text = header + string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "") + footer;
            text = DiagnosticRedactor.Redact(text);

            if (text.Length > maxChars)
            {
                trimmed = true;
                // Redaction may lengthen text: drop oldest log lines first.
                var list = lines.ToList();
                while (list.Count > 0 && text.Length > maxChars)
                {
                    list.RemoveAt(0);
                    text = DiagnosticRedactor.Redact(header + string.Join("\n", list) + (list.Count > 0 ? "\n" : "") + footer);
                }
                if (text.Length > maxChars)
                {
                    int keep = maxChars - Marker.Length;
                    text = keep > 0 ? text.Substring(0, keep) + Marker : text.Substring(0, maxChars);
                }
            }
            return new BugReportResult(text, trimmed);
        }

        private static void AppendFailure(StringBuilder sb, DiagnosticFailure? f)
        {
            if (f == null)
            {
                sb.AppendLine("None recorded.");
                return;
            }
            sb.AppendLine("- Time (UTC): " + f.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("- Operation: " + f.Operation);
            sb.AppendLine("- Error: " + f.Message);
            if (f.ExceptionType != null) sb.AppendLine("- Exception: " + f.ExceptionType);
            foreach (var d in f.Details)
            {
                var ls = d.Replace("\r", "").Split('\n');
                sb.AppendLine("- " + ls[0]);
                foreach (var l in ls.Skip(1)) sb.AppendLine("  " + l);
            }
            if (!string.IsNullOrEmpty(f.StackTrace))
            {
                string st = f.StackTrace!.Length > MaxStackInReport ? f.StackTrace.Substring(0, MaxStackInReport) + "..." : f.StackTrace;
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(st);
                sb.AppendLine("```");
            }
        }

        private static void AppendQuestTools(StringBuilder sb, string? dir)
        {
            try
            {
                dir ??= QuestService.GetQuestToolsCacheDirectory();
                foreach (var (name, present) in QuestToolsInstaller.Default.GetComponentStatus(dir))
                    sb.AppendLine($"- {name}: {(present ? "present" : "missing")}");
            }
            catch
            {
                sb.AppendLine("- status unavailable");
            }
        }

        private static string AppVersion()
        {
            var v = typeof(BugReportBuilder).Assembly.GetName().Version;
            return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        // Three-part version so it is never mistaken for an IPv4 address.
        private static string WindowsVersion()
        {
            var v = Environment.OSVersion.Version;
            return $"Windows {v.Major}.{v.Minor}.{v.Build}";
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return "unknown"; }
        }

        private static string Safe(string s) => s;
    }
}
