using System;
using System.Collections.Generic;

namespace USM.Core.Services
{
    /// <summary>One released version and the localization keys of its change notes.</summary>
    public sealed record ChangelogEntry(string Version, IReadOnlyList<string> Keys);

    /// <summary>Embedded changelog (works offline) plus the rule deciding when to show it.</summary>
    public static class WhatsNewService
    {
        /// <summary>Newest first.</summary>
        public static IReadOnlyList<ChangelogEntry> Entries { get; } = new[]
        {
            new ChangelogEntry("1.0.1", new[]
            {
                "WhatsNew_1_0_1_1",
                "WhatsNew_1_0_1_2",
                "WhatsNew_1_0_1_3",
                "WhatsNew_1_0_1_4",
                "WhatsNew_1_0_1_5",
                "WhatsNew_1_0_1_6",
            }),
        };

        public static bool HasEntry(string? version)
        {
            if (!TryParse(version, out var v)) return false;
            foreach (var e in Entries)
                if (TryParse(e.Version, out var ev) && ev == v) return true;
            return false;
        }

        /// <summary>Parses "1.0.1", "v1.0.1" or "1.0.1-beta" into a normalized 4-part version.</summary>
        public static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) t = t.Substring(0, cut);
            if (!Version.TryParse(t, out var parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
            return true;
        }

        /// <summary>
        /// last seen &lt; current: show. Equal or newer: no. No last seen: show only when a config file
        /// already existed (upgrade from v1.0, which stored nothing); a fresh install does not show.
        /// Unparsable values never show.
        /// </summary>
        public static bool ShouldShow(string? lastSeenVersion, string currentVersion, bool configExistedAtStartup)
        {
            if (!TryParse(currentVersion, out var current)) return false;
            if (string.IsNullOrWhiteSpace(lastSeenVersion)) return configExistedAtStartup;
            if (!TryParse(lastSeenVersion, out var last)) return false;
            return last < current;
        }

        /// <summary>ShouldShow plus the requirement that the changelog has notes for the current version.</summary>
        public static bool ShouldShowNow(string? lastSeenVersion, string currentVersion, bool configExistedAtStartup) =>
            HasEntry(currentVersion) && ShouldShow(lastSeenVersion, currentVersion, configExistedAtStartup);
    }
}
