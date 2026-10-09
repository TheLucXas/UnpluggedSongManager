using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace USM.Core.Services
{
    public class UsmPacker
    {
        public static void Pack(string inputDir, string outputFile, string metaJsonContent)
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);

            using var archive = ZipFile.Open(outputFile, ZipArchiveMode.Create);

            // 1. Write meta.json first so it can be streamed quickly by the Quest Mod
            var metaEntry = archive.CreateEntry("meta.json", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(metaEntry.Open()))
            {
                writer.Write(metaJsonContent);
            }

            // 2. Add all OGG, JPG, and JSON files
            foreach (var file in Directory.GetFiles(inputDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".ogg" || ext == ".jpg" || ext == ".json" || ext == ".xml")
                {
                    string name = Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, name, ext == ".ogg" || ext == ".jpg" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                }
            }
        }

        public static void Unpack(string inputFile, string outputDir)
        {
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
            using var archive = ZipFile.OpenRead(inputFile);
            foreach (var entry in archive.Entries)
            {
                string destFile = Path.Combine(outputDir, entry.FullName);
                entry.ExtractToFile(destFile, true);
            }
        }
    }
}
