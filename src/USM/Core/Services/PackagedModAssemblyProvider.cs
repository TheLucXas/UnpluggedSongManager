using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace USM.Core.Services
{
    public static class PackagedModAssemblyProvider
    {
        private const string QuestResourceName = "USM.EmbeddedMods.UnpluggedCustomSongs.Quest.dll";
        private const string PcResourceName = "USM.EmbeddedMods.UnpluggedCustomSongs.PC.dll";
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, string> ExtractedPaths = new(StringComparer.Ordinal);

        public static string? ExtractQuestModAssembly() => Extract(
            QuestResourceName, "UnpluggedCustomSongs.Quest.dll", "UnpluggedCustomSongs.Quest");

        public static string? ExtractPcModAssembly() => Extract(
            PcResourceName, "UnpluggedCustomSongs.PC.dll", "UnpluggedCustomSongs");

        private static string? Extract(string resourceName, string fileName, string expectedAssemblyName)
        {
            lock (SyncRoot)
            {
                if (ExtractedPaths.TryGetValue(resourceName, out string? cachedPath) && File.Exists(cachedPath))
                    return cachedPath;

                string? tempPath = null;
                try
                {
                    using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (resource == null) return null;

                    string outputDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "USM", "EmbeddedMods");
                    Directory.CreateDirectory(outputDirectory);

                    string outputPath = Path.Combine(outputDirectory, fileName);
                    tempPath = Path.Combine(outputDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp");
                    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        resource.CopyTo(output);

                    string? actualAssemblyName = AssemblyName.GetAssemblyName(tempPath).Name;
                    if (!string.Equals(actualAssemblyName, expectedAssemblyName, StringComparison.OrdinalIgnoreCase))
                        return null;

                    File.Move(tempPath, outputPath, overwrite: true);
                    tempPath = null;
                    ExtractedPaths[resourceName] = outputPath;
                    return outputPath;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (tempPath != null)
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
        }
    }
}
