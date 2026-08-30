using System.IO;
using System.Reflection;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Runtime;

internal sealed class EmbeddedInstallerReleaseManifest
{
    private const string ResourceName = "ClashSharp.Installer.ReleaseManifest.json";

    private EmbeddedInstallerReleaseManifest(
        byte[] bytes,
        InstallerReleaseManifest manifest)
    {
        Bytes = bytes;
        Manifest = manifest;
    }

    internal ReadOnlyMemory<byte> Bytes { get; }

    internal InstallerReleaseManifest Manifest { get; }

    internal static EmbeddedInstallerReleaseManifest Load() =>
        Load(typeof(EmbeddedInstallerReleaseManifest).Assembly);

    internal static EmbeddedInstallerReleaseManifest Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string[] matches = assembly
            .GetManifestResourceNames()
            .Where(name => string.Equals(name, ResourceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1
            || !string.Equals(matches[0], ResourceName, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.manifest_missing");
        }

        using Stream stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InstallerProtocolException("installer.release.manifest_missing");
        if (!stream.CanRead
            || stream.Length is <= 0 or > InstallerPayloadBudgets.MaximumManifestBytes)
        {
            throw new InstallerProtocolException("installer.release.manifest_size_invalid");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes.AsSpan(offset));
            if (read == 0)
            {
                throw new InstallerProtocolException("installer.release.manifest_size_invalid");
            }

            offset = checked(offset + read);
        }

        if (stream.ReadByte() != -1)
        {
            throw new InstallerProtocolException("installer.release.manifest_size_invalid");
        }

        InstallerReleaseManifest manifest = InstallerReleaseManifestCodec.Parse(bytes);
        return new EmbeddedInstallerReleaseManifest(bytes, manifest);
    }
}
