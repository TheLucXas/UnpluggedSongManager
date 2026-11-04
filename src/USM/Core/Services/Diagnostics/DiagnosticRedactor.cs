using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace USM.Core.Services.Diagnostics
{
    /// <summary>
    /// Removes personal and secret data from any text that is logged, written or copied.
    /// Never throws: if redaction itself fails the text is replaced by a placeholder.
    /// </summary>
    public static class DiagnosticRedactor
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        private const RegexOptions Base = RegexOptions.CultureInvariant;

        private static readonly object SerialGate = new();
        private static readonly HashSet<string> Serials = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registers an exact value (for example an ADB serial) that must always be masked.</summary>
        public static void RegisterSensitiveValue(string? value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                value = value.Trim();
                if (value.Length < 4) return;
                lock (SerialGate) Serials.Add(value);
            }
            catch { }
        }

        private static Regex R(string pattern, RegexOptions extra = RegexOptions.None) =>
            new(pattern, Base | extra, Timeout);

        private static readonly Regex AuthHeader = R(@"(authorization\s*[:=]\s*)(?:(?:bearer|basic|token)\s+)?[^\s,;""']+", RegexOptions.IgnoreCase);
        private static readonly Regex BearerValue = R(@"\bbearer\s+[A-Za-z0-9._~+/=\-]{6,}", RegexOptions.IgnoreCase);
        private static readonly Regex GitHubToken = R(@"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})");
        private static readonly Regex Url = R(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>]+");
        private static readonly Regex SecretParam = R(
            @"(?<![A-Za-z0-9])((?:[A-Za-z0-9]+[_.\-])*(?:[A-Za-z0-9]*(?:token|secret|password|passwd|signature)|key|apikey|pwd|credentials?)(?:[_.\-][A-Za-z0-9]+)*[""']?\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s&;,""']+)",
            RegexOptions.IgnoreCase);
        private static readonly Regex Email = R(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}");
        private static readonly Regex Mac = R(@"(?<![0-9A-Fa-f:\-])(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:\-])");
        private static readonly Regex Ipv6Candidate = R(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])");
        private static readonly Regex Ipv4 = R(
            @"(?<![\w./])(?<!version=)(?<!version )(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?::\d{1,5})?(?!\w|\.\d)",
            RegexOptions.IgnoreCase);
        private static readonly Regex SerialHeuristic = R(@"\b(?!NETSDK|MSBUILD)(?=[A-Z0-9]{10,20}\b)(?=[A-Z0-9]*[A-Z])(?=[A-Z0-9]*\d)[A-Z0-9]{10,20}\b");
        private static readonly Regex DrivePath = R(@"(?<![\w])[A-Za-z]:[\\/](?:[^\\/\r\n""'<>|*?:]+[\\/])*[^\s\\/""'<>|*?:,;)\]]*");
        private static readonly Regex UncPath = R(@"(?<![\w\\])\\\\[^\s\\/""'<>|*?:]+(?:\\[^\s\\/""'<>|*?:]+)*\\?");

        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            try
            {
                string s = text;
                s = AuthHeader.Replace(s, "$1<redacted>");
                s = BearerValue.Replace(s, "Bearer <redacted>");
                s = GitHubToken.Replace(s, "<github-token>");
                s = Url.Replace(s, StripUrl);
                s = SecretParam.Replace(s, "$1<redacted>");
                s = Email.Replace(s, "<email>");
                s = Mac.Replace(s, "<mac>");
                s = ReplaceIpv6(s);
                s = Ipv4.Replace(s, "<ip>");
                s = ReplaceKnownSerials(s);
                s = SerialHeuristic.Replace(s, "<device-serial>");
                s = ReplaceKnownFolders(s);
                s = DrivePath.Replace(s, m => ShortPath(m.Value));
                s = UncPath.Replace(s, m => ShortPath(m.Value));
                s = ReplaceWholeWord(s, SafeUserName(), "<user>");
                s = ReplaceWholeWord(s, SafeMachineName(), "<machine>");
                return s;
            }
            catch
            {
                return "[redaction failed]";
            }
        }

        private static string StripUrl(Match m)
        {
            string u = m.Value;
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            int authorityStart = scheme + 3;
            int cut = u.IndexOfAny(new[] { '?', '#' }, authorityStart);
            string head = cut >= 0 ? u.Substring(0, cut) : u;
            string tail = cut >= 0 ? (u[cut] == '?' ? "?<redacted>" : "#<redacted>") : "";
            int slash = head.IndexOf('/', authorityStart);
            string authority = slash >= 0 ? head.Substring(authorityStart, slash - authorityStart) : head.Substring(authorityStart);
            int at = authority.LastIndexOf('@');
            if (at >= 0)
                head = head.Substring(0, authorityStart) + "<credentials>@" + head.Substring(authorityStart + at + 1);
            return head + tail;
        }

        private static string ReplaceIpv6(string s) => Ipv6Candidate.Replace(s, m =>
        {
            string v = m.Value;
            if (v.Length < 3 || v.Count(c => c == ':') < 2) return v;
            return IPAddress.TryParse(v, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "<ip>" : v;
        });

        private static string ReplaceKnownSerials(string s)
        {
            string[] values;
            lock (SerialGate) values = Serials.OrderByDescending(v => v.Length).ToArray();
            foreach (var v in values)
                s = s.Replace(v, "<device-serial>", StringComparison.OrdinalIgnoreCase);
            return s;
        }

        private static string ShortPath(string path)
        {
            string trimmed = path.TrimEnd('\\', '/');
            int i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            string name = i >= 0 && i < trimmed.Length - 1 ? trimmed.Substring(i + 1) : string.Empty;
            if (name.Length == 0 || name.EndsWith(':')) return "<path>";
            return path.EndsWith('\\') || path.EndsWith('/') ? "<path>\\" : "<path>\\" + name;
        }

        private static string ReplaceKnownFolders(string s)
        {
            var folders = new List<(string Path, string Token)>();
            void Add(string? p, string token)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                p = p.TrimEnd('\\', '/');
                if (p.Length < 5) return;
                folders.Add((p, token));
            }
            try
            {
                Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
                Add(System.IO.Path.GetTempPath(), "%TEMP%");
                Add(Environment.GetEnvironmentVariable("TEMP"), "%TEMP%");
                Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            }
            catch { }
            foreach (var (p, token) in folders.OrderByDescending(f => f.Path.Length))
            {
                s = s.Replace(p, token, StringComparison.OrdinalIgnoreCase);
                s = s.Replace(p.Replace('\\', '/'), token, StringComparison.OrdinalIgnoreCase);
            }
            return s;
        }

        private static string? SafeUserName() { try { return Environment.UserName; } catch { return null; } }
        private static string? SafeMachineName() { try { return Environment.MachineName; } catch { return null; } }

        private static string ReplaceWholeWord(string s, string? word, string token)
        {
            if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return s;
            return Regex.Replace(s, @"(?<![\w])" + Regex.Escape(word) + @"(?![\w])", token,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        }
    }
}
