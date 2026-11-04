using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using USM.Core.Services.Diagnostics;
using USM.Core.Services.Quest;

namespace USM.Tests
{
    /// <summary>
    /// Manual check: runs USM's real patching pipeline on a local APK, with no device, so the
    /// install step is skipped. Usage: --patch-apk input.apk outputFolder
    /// </summary>
    internal static class PatchApkTest
    {
        public static async Task<int> RunAsync(string[] args)
        {
            int index = Array.IndexOf(args, "--patch-apk");
            if (index < 0 || args.Length < index + 3)
            {
                Console.WriteLine("Usage: --patch-apk <input.apk> <outputFolder>");
                return 2;
            }

            string input = Path.GetFullPath(args[index + 1]);
            string output = Path.GetFullPath(args[index + 2]);
            Directory.CreateDirectory(output);
            DiagnosticLog.Configure(Path.Combine(output, "logs"));

            string? finalApk = null;
            var service = new QuestService();
            bool ok = await service.PatchAndDeployAsync(
                serial: null,
                customSourceApk: input,
                skipIntroAndUnlockCampaign: false,
                progress: new Progress<(int Percent, string Status)>(p => Console.WriteLine($"[{p.Percent,3}%] {p.Status}")),
                log: message =>
                {
                    Console.WriteLine(message);
                    const string marker = "The modded APK is ready at:\n";
                    int at = message.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0) finalApk = message.Substring(at + marker.Length).Trim();
                },
                cancellationToken: CancellationToken.None);

            Console.WriteLine($"PIPELINE RESULT: {ok}");
            if (!ok || finalApk == null || !File.Exists(finalApk))
            {
                Console.WriteLine("No final APK was produced.");
                return 1;
            }

            string copy = Path.Combine(output, "final.apk");
            File.Copy(finalApk, copy, overwrite: true);
            Console.WriteLine($"FINAL APK: {new FileInfo(copy).Length} bytes");
            return 0;
        }
    }
}
