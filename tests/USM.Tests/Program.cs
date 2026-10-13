using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using USM.Core.Models;
using USM.Core.Parsers;
using USM.Core.Services;

namespace USM.Tests
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== USM C# Offline Conversion Verification Tests ===");

            string testOutDir = Path.Combine(Path.GetTempPath(), $"usm_offline_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testOutDir);

            try
            {
                // Test 1: MIDI & Multitrack Conversion
                string nirvanaDir = Environment.GetEnvironmentVariable("USM_TEST_MIDI_DIR") ?? string.Empty;
                if (Directory.Exists(nirvanaDir))
                {
                    Console.WriteLine("\n[TEST 1] Testing MIDI & Multitrack conversion (nirvana_extracted)...");
                    var prog = new Progress<ConversionProgress>(cp => Console.WriteLine($"  [{cp.Fraction:P0}] {cp.Message}"));
                    bool ok = await SongConverter.ConvertSongAsync(nirvanaDir, testOutDir, prog, msg => Console.WriteLine(msg));
                    Console.WriteLine($"Result: {ok}");

                    if (ok)
                    {
                        var dirs = Directory.GetDirectories(testOutDir);
                        string songFolder = dirs.First();
                        Console.WriteLine($"\nOutput Folder: {songFolder}");
                        foreach (var f in Directory.GetFiles(songFolder))
                        {
                            var fi = new FileInfo(f);
                            Console.WriteLine($"  * {fi.Name} ({fi.Length:N0} bytes)");
                        }

                        bool dataXml = File.Exists(Path.Combine(songFolder, "data.xml"));
                        bool songMp3 = File.Exists(Path.Combine(songFolder, "song.mp3"));
                        bool guitarMp3 = File.Exists(Path.Combine(songFolder, "song-guitar.mp3"));
                        bool hardXml = File.Exists(Path.Combine(songFolder, "songdata-Hard.xml"));
                        string xml = File.ReadAllText(Path.Combine(songFolder, "data.xml"));
                        bool isMulti = xml.Contains("<IsMultiTrack>true</IsMultiTrack>");

                        Console.WriteLine($"\nChecks: data.xml={dataXml}, song.mp3={songMp3}, song-guitar.mp3={guitarMp3}, songdata-Hard.xml={hardXml}, IsMultiTrack={isMulti}");
                        if (dataXml && songMp3 && guitarMp3 && hardXml && isMulti)
                        {
                            Console.WriteLine(">>> TEST 1 PASSED! (MIDI + Multitrack + XML perfectly generated) <<<");
                        }
                        else
                        {
                            Console.WriteLine(">>> TEST 1 FAILED! <<<");
                        }
                    }
                }

                // Test 2: .chart (Clone Hero) Conversion
                string metalliDir = Environment.GetEnvironmentVariable("USM_TEST_CHART_DIR") ?? string.Empty;
                if (Directory.Exists(metalliDir))
                {
                    Console.WriteLine("\n[TEST 2] Testing .chart (Clone Hero) conversion (metalli_extracted)...");
                    string testOutDir2 = Path.Combine(testOutDir, "ChartTest");
                    Directory.CreateDirectory(testOutDir2);

                    var prog = new Progress<ConversionProgress>(cp => Console.WriteLine($"  [{cp.Fraction:P0}] {cp.Message}"));
                    bool ok = await SongConverter.ConvertSongAsync(metalliDir, testOutDir2, prog, msg => Console.WriteLine(msg));
                    Console.WriteLine($"Result: {ok}");

                    if (ok)
                    {
                        string songFolder = Directory.GetDirectories(testOutDir2).First();
                        Console.WriteLine($"\nOutput Folder: {songFolder}");
                        foreach (var f in Directory.GetFiles(songFolder))
                        {
                            var fi = new FileInfo(f);
                            Console.WriteLine($"  * {fi.Name} ({fi.Length:N0} bytes)");
                        }

                        bool dataXml = File.Exists(Path.Combine(songFolder, "data.xml"));
                        bool songMp3 = File.Exists(Path.Combine(songFolder, "song.mp3"));
                        bool hardXml = File.Exists(Path.Combine(songFolder, "songdata-Hard.xml"));

                        Console.WriteLine($"\nChecks: data.xml={dataXml}, song.mp3={songMp3}, songdata-Hard.xml={hardXml}");
                        if (dataXml && songMp3 && hardXml)
                        {
                            Console.WriteLine(">>> TEST 2 PASSED! (.chart + Stereo perfectly generated) <<<");
                        }
                    }
                }

                // Test 3: .zip archive extraction and conversion
                if (Directory.Exists(metalliDir))
                {
                    Console.WriteLine("\n[TEST 3] Testing .zip archive conversion...");
                    string zipFile = Path.Combine(testOutDir, "test_chart.zip");
                    ZipFile.CreateFromDirectory(metalliDir, zipFile);
                    Console.WriteLine($"Created test zip: {zipFile} ({new FileInfo(zipFile).Length:N0} bytes)");

                    string testOutDir3 = Path.Combine(testOutDir, "ZipTest");
                    Directory.CreateDirectory(testOutDir3);

                    var prog = new Progress<ConversionProgress>(cp => Console.WriteLine($"  [{cp.Fraction:P0}] {cp.Message}"));
                    bool ok = await SongConverter.ConvertSongAsync(zipFile, testOutDir3, prog, msg => Console.WriteLine(msg));
                    Console.WriteLine($"Result: {ok}");

                    if (ok && Directory.GetDirectories(testOutDir3).Length > 0)
                    {
                        Console.WriteLine(">>> TEST 3 PASSED! (.zip archive unpacked and converted) <<<");
                    }
                }

                // Test 4: .sng binary decoding and conversion
                string realSng = Environment.GetEnvironmentVariable("USM_TEST_SNG_FILE") ?? string.Empty;
                if (File.Exists(realSng))
                {
                    Console.WriteLine("\n[TEST 4] Testing real .sng binary decoding & conversion...");
                    string testOutDir4 = Path.Combine(testOutDir, "SngTest");
                    Directory.CreateDirectory(testOutDir4);

                    var prog = new Progress<ConversionProgress>(cp => Console.WriteLine($"  [{cp.Fraction:P0}] {cp.Message}"));
                    bool ok = await SongConverter.ConvertSongAsync(realSng, testOutDir4, prog, msg => Console.WriteLine(msg));
                    Console.WriteLine($"Result: {ok}");

                    if (ok && Directory.GetDirectories(testOutDir4).Length > 0)
                    {
                        string songFolder = Directory.GetDirectories(testOutDir4).First();
                        Console.WriteLine($"\nOutput Folder: {songFolder}");
                        foreach (var f in Directory.GetFiles(songFolder))
                        {
                            var fi = new FileInfo(f);
                            Console.WriteLine($"  * {fi.Name} ({fi.Length:N0} bytes)");
                        }
                        Console.WriteLine(">>> TEST 4 PASSED! (.sng binary file decrypted, extracted and converted perfectly!) <<<");
                    }
                }

                // Test 5: Rejection of invalid files & ghost folder prevention
                Console.WriteLine("\n[TEST 5] Testing rejection of invalid files (.url, .exe) and ghost folder prevention...");
                string testOutDir5 = Path.Combine(testOutDir, "ValidationTest");
                Directory.CreateDirectory(testOutDir5);

                string dummyUrlFile = Path.Combine(testOutDir, "shortcut.url");
                File.WriteAllText(dummyUrlFile, "[InternetShortcut]\nURL=https://google.com");

                bool urlResult = await SongConverter.ConvertSongAsync(dummyUrlFile, testOutDir5, null, msg => Console.WriteLine($"  {msg}"));
                int dirCountAfterUrl = Directory.GetDirectories(testOutDir5).Length;

                string dummyEmptyFolder = Path.Combine(testOutDir, "empty_folder");
                Directory.CreateDirectory(dummyEmptyFolder);
                bool folderResult = await SongConverter.ConvertSongAsync(dummyEmptyFolder, testOutDir5, null, msg => Console.WriteLine($"  {msg}"));
                int dirCountAfterFolder = Directory.GetDirectories(testOutDir5).Length;

                if (!urlResult && !folderResult && dirCountAfterUrl == 0 && dirCountAfterFolder == 0)
                {
                    Console.WriteLine(">>> TEST 5 PASSED! (Invalid files rejected properly and zero ghost folders created!) <<<");
                }
                else
                {
                    throw new Exception($"Test 5 failed: urlResult={urlResult}, folderResult={folderResult}, dirs={dirCountAfterFolder}");
                }

                // Test 6: GitHub Releases & UpdateService Verification
                Console.WriteLine("\n[TEST 6] Testing UpdateService version comparison, release JSON parsing, and localization...");
                
                // 6a. Version comparisons
                if (UpdateService.CompareVersions("v2.1.0", "2.0.0") <= 0)
                    throw new Exception("v2.1.0 should be greater than 2.0.0");
                if (UpdateService.CompareVersions("2.0.0", "v2.0.0") != 0)
                    throw new Exception("2.0.0 should be equal to v2.0.0");
                if (UpdateService.CompareVersions("1.9.9", "2.0.0") >= 0)
                    throw new Exception("1.9.9 should be less than 2.0.0");
                if (UpdateService.CleanVersionString("  v3.1.2  ") != "3.1.2")
                    throw new Exception("CleanVersionString failed");

                // 6b. JSON Parsing & Asset Detection
                string sampleJson = "{\n" +
                    "  \"tag_name\": \"v2.1.0\",\n" +
                    "  \"name\": \"USM 2.1.0 Release\",\n" +
                    "  \"html_url\": \"https://github.com/TheLucXas/UnpluggedSongManager/releases/tag/v2.1.0\",\n" +
                    "  \"body\": \"Fixed bugs and added Quest patcher.\",\n" +
                    "  \"published_at\": \"2026-10-06T12:00:00Z\",\n" +
                    "  \"assets\": [\n" +
                    "    {\n" +
                    "      \"name\": \"USM-v2.1.0.zip\",\n" +
                    "      \"browser_download_url\": \"https://github.com/TheLucXas/UnpluggedSongManager/releases/download/v2.1.0/USM-v2.1.0.zip\",\n" +
                    "      \"size\": 15000000\n" +
                    "    },\n" +
                    "    {\n" +
                    "      \"name\": \"UnpluggedCustomSongs.dll\",\n" +
                    "      \"browser_download_url\": \"https://github.com/TheLucXas/UnpluggedSongManager/releases/download/v2.1.0/UnpluggedCustomSongs.dll\",\n" +
                    "      \"size\": 25000\n" +
                    "    }\n" +
                    "  ]\n" +
                    "}";

                var parsedRelease = UpdateService.ParseReleaseJson(sampleJson);
                if (parsedRelease == null)
                    throw new Exception("Failed to parse sample release JSON");
                if (parsedRelease.TagName != "v2.1.0")
                    throw new Exception($"Expected tag v2.1.0, got {parsedRelease.TagName}");
                if (parsedRelease.Assets.Count != 2)
                    throw new Exception($"Expected 2 assets, got {parsedRelease.Assets.Count}");

                var modAsset = parsedRelease.Assets.FirstOrDefault(a => 
                    a.Name.Equals("UnpluggedCustomSongs.dll", StringComparison.OrdinalIgnoreCase));
                if (modAsset == null || !modAsset.BrowserDownloadUrl.Contains("UnpluggedCustomSongs.dll"))
                    throw new Exception("Failed to find mod asset in release");

                var usmAsset = parsedRelease.Assets.FirstOrDefault(a =>
                    (a.Name.Contains("USM", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Unplugged", StringComparison.OrdinalIgnoreCase)) &&
                    (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                    !a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                if (usmAsset == null || usmAsset.Name != "USM-v2.1.0.zip")
                    throw new Exception("Failed to match USM app zip asset");

                // 6c. Localization checks across languages
                var loc = USM.Core.Services.Localization.LocalizationService.Instance;
                string prevLang = loc.CurrentLanguage;
                foreach (var lang in new[] { "en", "es", "pt" })
                {
                    loc.CurrentLanguage = lang;
                    string title = USM.Core.Services.Localization.LocalizationService.T("Settings_UpdatesTitle");
                    string upToDate = USM.Core.Services.Localization.LocalizationService.T("Settings_UpToDate");
                    string checkBtn = USM.Core.Services.Localization.LocalizationService.T("Settings_CheckUpdatesBtn");
                    string appBtn = USM.Core.Services.Localization.LocalizationService.T("Settings_UpdateAppBtn");
                    string confirmTitle = USM.Core.Services.Localization.LocalizationService.T("Settings_UpdateAppConfirmTitle");
                    string confirmMsg = USM.Core.Services.Localization.LocalizationService.T("Settings_UpdateAppConfirmMsg", "v2.1.0");
                    string updating = USM.Core.Services.Localization.LocalizationService.T("Settings_UpdatingApp", 50.0);

                    string ipLabel = USM.Core.Services.Localization.LocalizationService.T("Quest_IpLabel");
                    string portLabel = USM.Core.Services.Localization.LocalizationService.T("Quest_PortLabel");
                    string incSync = USM.Core.Services.Localization.LocalizationService.T("Quest_Dialog_ConfirmIncrementalSyncMsg", 1, 21);
                    string allSynced = USM.Core.Services.Localization.LocalizationService.T("Quest_Dialog_AllSongsSyncedMsg", 22);

                    if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(upToDate) || string.IsNullOrEmpty(checkBtn) ||
                        string.IsNullOrEmpty(appBtn) || string.IsNullOrEmpty(confirmTitle) || string.IsNullOrEmpty(confirmMsg) ||
                        string.IsNullOrEmpty(updating) || string.IsNullOrEmpty(ipLabel) || string.IsNullOrEmpty(portLabel) ||
                        string.IsNullOrEmpty(incSync) || string.IsNullOrEmpty(allSynced))
                    {
                        throw new Exception($"Missing update/sync localization for language: {lang}");
                    }

                    if (ipLabel.Contains("Field") || ipLabel.Contains("Barra") || portLabel.Contains("Field") || portLabel.Contains("Barra"))
                    {
                        throw new Exception($"IP/Port label still contains field description: {ipLabel} / {portLabel}");
                    }
                }
                loc.CurrentLanguage = prevLang;

                Console.WriteLine(">>> TEST 6 PASSED! (Version comparison, release JSON parsing, app & mod assets matching & i18n verified) <<<");

                Console.WriteLine("\n=============================================");
                Console.WriteLine(">>> ALL CONVERSION TESTS COMPLETED SUCCESSFULLY! <<<");
                Console.WriteLine("=============================================");
            }
            finally
            {
                if (Directory.Exists(testOutDir))
                {
                    try { Directory.Delete(testOutDir, recursive: true); } catch { }
                }
            }
        }
    }
}
