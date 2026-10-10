using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace USM.Core.Services;

internal static class ModAssemblyMetadata
{
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
