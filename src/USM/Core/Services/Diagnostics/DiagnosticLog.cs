using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace USM.Core.Services.Diagnostics
{
    /// <summary>The most recent failure, already redacted.</summary>
    public sealed record DiagnosticFailure(
        DateTime TimeUtc,
        string Operation,
        string Message,
        string? ExceptionType,
        string? StackTrace,
        IReadOnlyList<string> Details);

    /// <summary>
    /// Small rotating text log (usm.log / usm.1.log, 1 MB each). Thread-safe and never throws.
    /// Every line is redacted before it is written.
    /// Line format: <c>yyyy-MM-dd HH:mm:ss LEVEL [operation] message</c>; a failure adds
    /// indented continuation lines (exception, stack, details).
    /// </summary>
    public static class DiagnosticLog
    {
        public const long MaxFileBytes = 1024 * 1024;
        private const string Current = "usm.log";
        private const string Previous = "usm.1.log";
        private const string Stamp = "yyyy-MM-dd HH:mm:ss";
        private const int MaxStackChars = 4000;
        private const int MaxDetailChars = 4000;

        private static readonly object Gate = new();
        private static string? _dir;
        private static DiagnosticFailure? _last;
        private static bool _lastLoaded;

        public static string LogDirectory
        {
            get
            {
                lock (Gate) return _dir ??= DefaultDirectory();
            }
        }

        private static string DefaultDirectory()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USM", "logs"); }
            catch { return Path.Combine(Path.GetTempPath(), "USM", "logs"); }
        }

        /// <summary>Overrides the log folder (tests). Null restores the default. Resets the in-memory failure.</summary>
        public static void Configure(string? directory)
        {
            lock (Gate)
            {
                _dir = string.IsNullOrWhiteSpace(directory) ? null : directory;
                _last = null;
                _lastLoaded = false;
            }
        }

        public static void Info(string operation, string message) => Write("INFO", operation, message, null);
        public static void Warn(string operation, string message) => Write("WARN", operation, message, null);

        /// <summary>Logs an error. <paramref name="details"/> are "Key=Value" strings.</summary>
        public static void Failure(string operation, string message, Exception? ex = null, params string?[] details)
        {
            try
            {
                string op = Clean(operation);
                string msg = DiagnosticRedactor.Redact(Flatten(message));
                string? type = ex?.GetType().FullName;
                string? stack = null;
                if (ex != null)
                {
                    string full;
                    try { full = ex.ToString(); } catch { full = type ?? ""; }
                    if (full.Length > MaxStackChars) full = full.Substring(0, MaxStackChars) + "...";
                    stack = DiagnosticRedactor.Redact(full);
                }
                var det = new List<string>();
                if (details != null)
                    foreach (var d in details)
                    {
                        if (string.IsNullOrWhiteSpace(d)) continue;
                        string t = d!.Length > MaxDetailChars ? d.Substring(d.Length - MaxDetailChars) : d;
                        det.Add(DiagnosticRedactor.Redact(t).TrimEnd());
                    }

                var now = DateTime.Now;
                var sb = new StringBuilder();
                sb.Append(now.ToString(Stamp, CultureInfo.InvariantCulture)).Append(" ERROR [").Append(op).Append("] ").Append(msg).Append('\n');
                if (type != null) sb.Append("    exception: ").Append(type).Append('\n');
                if (stack != null)
                {
                    sb.Append("    stack:\n");
                    foreach (var l in SplitLines(stack)) sb.Append("      ").Append(l).Append('\n');
                }
                foreach (var d in det)
                {
                    var lines = SplitLines(d);
                    sb.Append("    detail: ").Append(lines.FirstOrDefault() ?? "").Append('\n');
                    foreach (var l in lines.Skip(1)) sb.Append("      ").Append(l).Append('\n');
                }

                var failure = new DiagnosticFailure(now.ToUniversalTime(), op, msg, type, stack, det);
                lock (Gate)
                {
                    _last = failure;
                    _lastLoaded = true;
                    Append(sb.ToString());
                }
            }
            catch { }
        }

        /// <summary>Most recent failure: this run's, or the last one found in the log files.</summary>
        public static DiagnosticFailure? LastFailure
        {
            get
            {
                try
                {
                    lock (Gate)
                    {
                        if (_last != null) return _last;
                        if (_lastLoaded) return null;
                        _lastLoaded = true;
                        _last = ParseLastFailure();
                        return _last;
                    }
                }
                catch { return null; }
            }
        }

        /// <summary>The newest log lines (oldest first) whose total size stays within <paramref name="maxChars"/>.</summary>
        public static IReadOnlyList<string> ReadRecentLines(int maxChars)
        {
            try
            {
                var all = ReadAllLines();
                var result = new List<string>();
                int total = 0;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    int len = all[i].Length + 1;
                    if (total + len > maxChars) break;
                    total += len;
                    result.Add(all[i]);
                }
                result.Reverse();
                return result;
            }
            catch { return Array.Empty<string>(); }
        }

        private static void Write(string level, string operation, string message, Exception? ex)
        {
            try
            {
                string line = DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture) + " " + level + " [" + Clean(operation) + "] "
                    + DiagnosticRedactor.Redact(Flatten(message)) + "\n";
                lock (Gate) Append(line);
            }
            catch { }
        }

        // Caller holds Gate.
        private static void Append(string text)
        {
            try
            {
                string dir = _dir ??= DefaultDirectory();
                Directory.CreateDirectory(dir);
                string cur = Path.Combine(dir, Current);
                var fi = new FileInfo(cur);
                if (fi.Exists && fi.Length >= MaxFileBytes)
                {
                    string prev = Path.Combine(dir, Previous);
                    try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                    File.Move(cur, prev);
                }
                using var fs = new FileStream(cur, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static List<string> ReadAllLines()
        {
            var lines = new List<string>();
            string dir;
            lock (Gate) dir = _dir ??= DefaultDirectory();
            foreach (var name in new[] { Previous, Current })
            {
                try
                {
                    string p = Path.Combine(dir, name);
                    if (!File.Exists(p)) continue;
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string? l;
                    while ((l = sr.ReadLine()) != null) lines.Add(l);
                }
                catch { }
            }
            return lines;
        }

        private static DiagnosticFailure? ParseLastFailure()
        {
            var lines = ReadAllLines();
            int start = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i].Length > 27 && lines[i].IndexOf(" ERROR [", 19, StringComparison.Ordinal) == 19) { start = i; break; }
            if (start < 0) return null;

            string header = lines[start];
            int close = header.IndexOf("] ", 27, StringComparison.Ordinal);
            if (close < 0) return null;
            string op = header.Substring(27, close - 27);
            string msg = header.Substring(close + 2);
            DateTime utc = DateTime.UtcNow;
            if (DateTime.TryParseExact(header.Substring(0, 19), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var t))
                utc = t;

            string? type = null;
            var stack = new List<string>();
            var details = new List<string>();
            string mode = "";
            for (int i = start + 1; i < lines.Count && lines[i].StartsWith("    ", StringComparison.Ordinal); i++)
            {
                string l = lines[i];
                if (l.StartsWith("    exception: ", StringComparison.Ordinal)) { type = l.Substring(15); mode = ""; }
                else if (l == "    stack:") mode = "stack";
                else if (l.StartsWith("    detail: ", StringComparison.Ordinal)) { details.Add(l.Substring(12)); mode = "detail"; }
                else if (l.StartsWith("      ", StringComparison.Ordinal))
                {
                    if (mode == "stack") stack.Add(l.Substring(6));
                    else if (mode == "detail" && details.Count > 0) details[^1] += "\n" + l.Substring(6);
                }
            }
            return new DiagnosticFailure(utc, op, msg, type, stack.Count > 0 ? string.Join("\n", stack) : null, details);
        }

        private static string Clean(string? operation)
        {
            string o = Flatten(operation ?? "App");
            return DiagnosticRedactor.Redact(o.Replace('[', '(').Replace(']', ')'));
        }

        private static string Flatten(string? s) =>
            (s ?? string.Empty).Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ');

        private static string[] SplitLines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
