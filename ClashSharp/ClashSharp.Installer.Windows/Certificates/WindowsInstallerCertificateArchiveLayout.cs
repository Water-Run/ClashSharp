using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>Maps one authenticated canonical SID to its existing private archive leaf.</summary>
internal static class WindowsInstallerCertificateArchiveLayout
{
    internal static string GetFileName(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        return $"certificate-owner-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(targetSid)))}.json";
    }
}
