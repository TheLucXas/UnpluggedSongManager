using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace USM.Core.Services.Quest
{
    /// <summary>
    /// Runs the LemonLoader (MelonLoader.Installer.Core) APK patcher by reflection, loading the
    /// downloaded DLLs from a cache folder. Invoked in a child process via
    /// <c>USM.exe --usm-lemon-patch ...</c>. Replaces the old lemon_patcher.exe wrapper.
    /// Arguments: targetApk outputDir tempDir melonDataZip packageName lemonPatcherDir.
    /// Exit codes: 0 ok, 1 usage, 2 APK missing, 3 melon_data missing, 4 patch failed, 5 exception.
    /// </summary>
    public static class LemonPatcherBridge
    {
        public const string CommandLineSwitch = "--usm-lemon-patch";
        private const string CoreDll = "MelonLoader.Installer.Core.dll";

        /// <summary>True when the patcher's output shows it could not reach the network.</summary>
        public static bool IsNetworkFailure(string output) =>
            output.Contains("SocketException", StringComparison.Ordinal) ||
            output.Contains("HttpRequestException", StringComparison.Ordinal);

        public class LoggerProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == "Log" && args is { Length: > 0 })
                    Console.WriteLine("[LemonLoader] " + args[0]);
                return null;
            }
        }

        public static int Run(string[] args)
        {
            try { return RunCore(args); }
            finally { try { Console.Out.Flush(); } catch { } }
        }

        private static int RunCore(string[] args)
        {
            if (args.Length < 6)
            {
                Console.WriteLine("Usage: --usm-lemon-patch <targetApk> <outputDir> <tempDir> <melonDataZip> <packageName> <lemonPatcherDir>");
                return 1;
            }

            string targetApk = args[0], outputDir = args[1], tempDir = args[2];
            string melonDataZip = args[3], packageName = args[4], lemonDir = Path.GetFullPath(args[5]);

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

            Func<AssemblyLoadContext, AssemblyName, Assembly?> resolver = (ctx, name) =>
            {
                string candidate = Path.Combine(lemonDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            var handler = new Func<AssemblyLoadContext, AssemblyName, Assembly?>(resolver);
            AssemblyLoadContext.Default.Resolving += handler;

            try
            {
                // A leftover temp directory makes the patcher fail with "file already exists".
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(tempDir);

                string tempMelonData = Path.Combine(tempDir, "melon_data.zip");
                File.Copy(melonDataZip, tempMelonData, true);

                string corePath = Path.Combine(lemonDir, CoreDll);
                if (!File.Exists(corePath))
                {
                    Console.WriteLine($"[LemonLoader] Exception: {CoreDll} not found in {lemonDir}");
                    return 5;
                }

                var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(corePath));
                var argsType = core.GetType("MelonLoader.Installer.Core.PatchArguments", true)!;
                var patcherType = core.GetType("MelonLoader.Installer.Core.Patcher", true)!;
                var loggerType = core.GetType("MelonLoader.Installer.Core.IPatchLogger", true)!;
                var primitives = Assembly.Load(new AssemblyName("AssetRipper.Primitives"));
                var unityVersionType = primitives.GetType("AssetRipper.Primitives.UnityVersion", true)!;

                object patchArgs = Activator.CreateInstance(argsType)!;
                void Set(string field, object? value) =>
                    (argsType.GetField(field) ?? throw new MissingMemberException(argsType.FullName, field)).SetValue(patchArgs, value);
                Set("TargetApkPath", targetApk);
                Set("OutputApkDirectory", outputDir);
                Set("TempDirectory", tempDir);
                Set("MelonDataPath", tempMelonData);
                Set("PackageName", packageName);
                Set("UnityVersion", Activator.CreateInstance(unityVersionType, (ushort)2021, (ushort)3, (ushort)10));

                object logger = DispatchProxy.Create(loggerType, typeof(LoggerProxy));
                var ctor = patcherType.GetConstructor(new[] { argsType, loggerType })
                    ?? throw new MissingMemberException(patcherType.FullName, ".ctor(PatchArguments, IPatchLogger)");
                var run = patcherType.GetMethod("Run", Type.EmptyTypes)
                    ?? throw new MissingMethodException(patcherType.FullName, "Run");

                object patcher = ctor.Invoke(new[] { patchArgs, logger });
                Console.WriteLine("[LemonLoader] Starting APK patch and LemonLoader injection...");
                bool success = (bool)run.Invoke(patcher, null)!;
                if (success)
                {
                    Console.WriteLine("[LemonLoader] APK patched and signed successfully!");
                    return 0;
                }

                Console.WriteLine("[LemonLoader] Patching failed.");
                return 4;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException { InnerException: not null } tie) ex = tie.InnerException!;
                Console.WriteLine($"[LemonLoader] Exception: {ex.GetType().FullName}: {ex.Message}");
                return 5;
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= handler;
            }
        }
    }
}
