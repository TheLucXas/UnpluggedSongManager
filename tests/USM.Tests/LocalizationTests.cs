using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using USM.Core.Services.Localization;

namespace USM.Tests
{
    /// <summary>Localization checks for the Quest tools, Report a bug and What's new strings: <c>--localization</c>.</summary>
    internal static class LocalizationTests
    {
        private static readonly string[] Keys =
        {
            "QuestTools_Verifying", "QuestTools_Downloading", "QuestTools_Extracting", "QuestTools_Ready",
            "QuestTools_ToastFailed", "QuestTools_DialogTitle", "QuestTools_FailedLog", "QuestTools_ErrNotFound",
            "QuestTools_ErrHttp", "QuestTools_ErrNetwork", "QuestTools_ErrIntegrity", "QuestTools_ErrDisk",
            "QuestTools_ErrUnexpected", "QuestTools_ModMissing", "Nav_ReportBug", "Nav_ReportBug_Tooltip",
            "ReportBug_Title", "ReportBug_Notice", "ReportBug_Generating", "ReportBug_Failed", "ReportBug_Trimmed",
            "ReportBug_Copy", "ReportBug_Copied", "ReportBug_CopyFailed", "ReportBug_OpenIssue",
            "ReportBug_OpenIssueFailed", "ReportBug_Close", "WhatsNew_Title", "WhatsNew_VersionHeader",
            "WhatsNew_Close", "Settings_WhatsNew", "Settings_WhatsNew_Tooltip", "WhatsNew_1_0_1_1",
            "WhatsNew_1_0_1_2", "WhatsNew_1_0_1_3", "WhatsNew_1_0_1_4", "WhatsNew_1_0_1_5", "WhatsNew_1_0_1_6",
        };

        // Exact English-identical values that are acceptable in a given language. Each entry is "lang|key".
        // Keep this list as short as possible: every translated value must differ from English.
        // "Version {0}" is the same word in French and German, so those two are accepted exact matches.
        private static readonly HashSet<string> AllowedSameAsEnglish = new()
        {
            "fr|WhatsNew_VersionHeader",
            "de|WhatsNew_VersionHeader",
        };

        private static readonly Regex PlaceholderRx = new(@"\{(\d+)(:[^}]*)?\}", RegexOptions.Compiled);

        private static string[] PlaceholderIndices(string text) =>
            PlaceholderRx.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();

        public static int Run(string[] args)
        {
            var loc = LocalizationService.Instance;
            string prevLang = loc.CurrentLanguage;
            int failures = 0;
            int checks = 0;

            void Check(bool ok, string name, string detail = "")
            {
                checks++;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
                if (!ok) failures++;
            }

            Console.WriteLine("=== USM Localization Tests (--localization) ===");

            // English reference values.
            loc.Initialize("en");
            var english = new Dictionary<string, string>();
            foreach (var key in Keys)
            {
                english[key] = loc.GetString(key);
            }

            var languages = LocalizationService.SupportedLanguages.Select(l => l.Code).ToList();
            Console.WriteLine($"Languages: {string.Join(", ", languages)} ({languages.Count})");
            Console.WriteLine($"Keys per language: {Keys.Length}");

            foreach (var code in languages)
            {
                Console.WriteLine($"\n[{code}]");
                loc.Initialize(code);

                foreach (var key in Keys)
                {
                    string text = loc.GetString(key);
                    string en = english[key];

                    Check(!string.IsNullOrWhiteSpace(text) && text != key, $"{code} {key} resolves", $"got '{text}'");

                    if (code == "en")
                    {
                        continue;
                    }

                    bool sameAsEnglish = text == en;
                    bool allowed = AllowedSameAsEnglish.Contains($"{code}|{key}");
                    Check(!sameAsEnglish || allowed, $"{code} {key} is translated", "identical to English");

                    var expectedIdx = PlaceholderIndices(en);
                    var actualIdx = PlaceholderIndices(text);
                    Check(expectedIdx.SequenceEqual(actualIdx), $"{code} {key} placeholders match",
                        $"en=[{string.Join(",", expectedIdx)}] {code}=[{string.Join(",", actualIdx)}]");

                    bool formatOk = true;
                    string formatErr = "";
                    try
                    {
                        _ = string.Format(text, "SAMPLE", 50.0, "SAMPLE2");
                    }
                    catch (Exception ex)
                    {
                        formatOk = false;
                        formatErr = ex.GetType().Name + ": " + ex.Message;
                    }
                    Check(formatOk, $"{code} {key} string.Format does not throw", formatErr);
                }
            }

            loc.Initialize(prevLang);

            Console.WriteLine($"\n{checks} checks, {failures} failure(s).");
            Console.WriteLine(failures == 0
                ? ">>> LOCALIZATION TESTS PASSED <<<"
                : ">>> LOCALIZATION TESTS FAILED <<<");
            return failures == 0 ? 0 : 1;
        }
    }
}
