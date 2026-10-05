using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace USM.Core.Parsers
{
    public class SngDecoder
    {
        public class SngFileEntry
        {
            public string FileName { get; set; } = string.Empty;
            public ulong ContentLength { get; set; }
            public ulong ContentIndex { get; set; }
        }

        public static Dictionary<string, string> Decode(string sngFilePath, string outputDir)
        {
            if (!File.Exists(sngFilePath))
                throw new FileNotFoundException($"SNG file not found: {sngFilePath}");

            Directory.CreateDirectory(outputDir);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var fs = new FileStream(sngFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            // 1. Header (6 bytes identifier + 4 bytes version + 16 bytes xor_mask)
            byte[] identBytes = reader.ReadBytes(6);
            string ident = Encoding.ASCII.GetString(identBytes);
            if (ident != "SNGPKG" && ident != "SNGAPP")
            {
                throw new InvalidDataException($"Invalid SNG file identifier: '{ident}'. Expected 'SNGPKG'.");
            }

            uint version = reader.ReadUInt32();
            byte[] xorMask = reader.ReadBytes(16);

            // 2. Metadata Section
            ulong metadataLen = reader.ReadUInt64();
            ulong metadataCount = reader.ReadUInt64();
            for (ulong i = 0; i < metadataCount; i++)
            {
                uint keyLen = reader.ReadUInt32();
                string key = Encoding.UTF8.GetString(reader.ReadBytes((int)keyLen));

                uint valLen = reader.ReadUInt32();
                string val = Encoding.UTF8.GetString(reader.ReadBytes((int)valLen));

                metadata[key] = val;
            }

            // 3. File Metadata Section
            ulong fileMetaLen = reader.ReadUInt64();
            ulong fileCount = reader.ReadUInt64();
            var fileEntries = new List<SngFileEntry>();

            for (ulong i = 0; i < fileCount; i++)
            {
                byte fnLen = reader.ReadByte();
                string fn = Encoding.UTF8.GetString(reader.ReadBytes(fnLen));
                ulong cLen = reader.ReadUInt64();
                ulong cIdx = reader.ReadUInt64();

                fileEntries.Add(new SngFileEntry
                {
                    FileName = fn,
                    ContentLength = cLen,
                    ContentIndex = cIdx
                });
            }

            // 4. File Data Section
            ulong fileDataLen = reader.ReadUInt64();

            const int chunkSize = 64 * 1024;
            byte[] buffer = new byte[chunkSize];

            foreach (var entry in fileEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.FileName)) continue;

                // Sanitize file name to prevent directory traversal
                string safeName = Path.GetFileName(entry.FileName);
                string destFile = Path.Combine(outputDir, safeName);

                fs.Seek((long)entry.ContentIndex, SeekOrigin.Begin);

                using (var outFs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ulong bytesReadTotal = 0;
                    while (bytesReadTotal < entry.ContentLength)
                    {
                        int toRead = (int)Math.Min((ulong)buffer.Length, entry.ContentLength - bytesReadTotal);
                        int read = fs.Read(buffer, 0, toRead);
                        if (read <= 0) break;

                        for (int i = 0; i < read; i++)
                        {
                            long fileBytePos = (long)bytesReadTotal + i;
                            byte xorKey = (byte)(xorMask[fileBytePos % 16] ^ (fileBytePos & 0xFF));
                            buffer[i] ^= xorKey;
                        }

                        outFs.Write(buffer, 0, read);
                        bytesReadTotal += (ulong)read;
                    }
                }
            }

            // If song.ini wasn't embedded, generate it from SNG metadata
            string songIniPath = Path.Combine(outputDir, "song.ini");
            if (!File.Exists(songIniPath) && metadata.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[song]");
                foreach (var kvp in metadata)
                {
                    sb.AppendLine($"{kvp.Key} = {kvp.Value}");
                }
                File.WriteAllText(songIniPath, sb.ToString(), Encoding.UTF8);
            }

            return metadata;
        }
    }
}
