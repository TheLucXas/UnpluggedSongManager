using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace USM.Core.Services;

internal static class ModAssemblyMetadata
{
    // Correct the development-only 1.1 label used before the first release.
    // Future published versions still retain the normal downgrade protection.
    public static bool IsDevelopmentVersionReset(string? target, string? installed) =>
        (target is "1.0" or "1.0.0") && (installed is "1.1" or "1.1.0");

    public static bool CanInstallPackagedVersion(string? target, string? installed) =>
        UpdateService.CompareVersions(target ?? "0", installed ?? "0") >= 0
        || IsDevelopmentVersionReset(target, installed);

    public static bool IsNewPackagedVersion(string? target, string? installed) =>
        !string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(installed)
        && Version.TryParse(UpdateService.CleanVersionString(target), out _)
        && Version.TryParse(UpdateService.CleanVersionString(installed), out _)
        && (UpdateService.CompareVersions(target, installed) > 0 || IsDevelopmentVersionReset(target, installed));

    public static string? GetHash(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            using var stream = File.OpenRead(path);
            return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch { return null; }
    }

    // Inspect the file without loading or executing a PC/IL2CPP mod in USM.
    public static string? GetVersion(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();
            var assembly = reader.GetAssemblyDefinition();
            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (reader.GetString(type.Namespace) != "MelonLoader" || reader.GetString(type.Name) != "MelonInfoAttribute") continue;
                var value = reader.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1) continue;
                value.ReadSerializedString(); // mod type
                value.ReadSerializedString(); // display name
                string? version = value.ReadSerializedString();
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            return assembly.Version.ToString();
        }
        catch { return null; }
    }
}
