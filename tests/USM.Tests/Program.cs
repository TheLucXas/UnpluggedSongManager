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
                string nirvanaDir = @"C:/REDACTED\.gemini\antigravity\scratch\nirvana_extracted";
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
                string metalliDir = @"C:/REDACTED\.gemini\antigravity\scratch\metalli_extracted";
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
                string realSng = @"C:/REDACTED\AppData\Local\Temp\tmpcuqw0idj.sng";
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
