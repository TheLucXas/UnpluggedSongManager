using System;
using System.IO;
using AssetRipper.Primitives;

namespace LemonPatcher
{
    class Logger : MelonLoader.Installer.Core.IPatchLogger
    {
        public void Log(string message) => Console.WriteLine("[LemonLoader] " + message);
    }

    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 5)
            {
                Console.WriteLine("Usage: lemon_patcher <targetApk> <outputDir> <tempDir> <melonDataZip> <packageName>");
                return 1;
            }

            string targetApk = args[0];
            string outputDir = args[1];
            string tempDir = args[2];
            string melonDataZip = args[3];
            string packageName = args[4];

            if (!File.Exists(targetApk))
            {
                Console.WriteLine($"Error: Target APK not found: {targetApk}");
                return 2;
            }
            if (!File.Exists(melonDataZip))
            {
                Console.WriteLine($"Error: melon_data.zip not found: {melonDataZip}");
                return 3;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(tempDir);

                string tempMelonData = Path.Combine(tempDir, "melon_data.zip");
                File.Copy(melonDataZip, tempMelonData, true);

                var patchArgs = new MelonLoader.Installer.Core.PatchArguments
                {
                    TargetApkPath = targetApk,
                    OutputApkDirectory = outputDir,
                    TempDirectory = tempDir,
                    MelonDataPath = tempMelonData,
                    PackageName = packageName,
                    UnityVersion = new UnityVersion(2021, 3, 10)
                };

                var patcher = new MelonLoader.Installer.Core.Patcher(patchArgs, new Logger());
                Console.WriteLine("[LemonLoader] Starting APK patch and LemonLoader injection...");
                bool success = patcher.Run();
                if (success)
                {
                    Console.WriteLine("[LemonLoader] APK patched and signed successfully!");
                    return 0;
                }
                else
                {
                    Console.WriteLine("[LemonLoader] Patching failed.");
                    return 4;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LemonLoader] Exception: {ex.Message}");
                return 5;
            }
        }
    }
}
