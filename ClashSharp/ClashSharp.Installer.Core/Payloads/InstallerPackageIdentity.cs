using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>Exact primary MSIX identity compiled into the signed installer manifest.</summary>
/// <param name="Name">Package Identity Name.</param>
/// <param name="Publisher">Canonical package Publisher distinguished name.</param>
/// <param name="PublisherId">Windows-derived 13-character publisher identifier.</param>
/// <param name="Architecture">Canonical package architecture; only x64 is accepted.</param>
/// <param name="ResourceId">Exact package resource identifier; empty for the primary package.</param>
/// <param name="PackageFullName">Exact full name derived from identity and release version.</param>
/// <param name="PackageFamilyName">Exact family name derived from name and publisher.</param>
/// <param name="ApplicationId">Exact primary Application Id.</param>
/// <param name="ApplicationExecutable">Exact primary executable path.</param>
/// <param name="ApplicationEntryPoint">Exact application entry point.</param>
public sealed record InstallerPackageIdentity(
    string Name,
    string Publisher,
    string PublisherId,
    string Architecture,
    string ResourceId,
    string PackageFullName,
    string PackageFamilyName,
    string ApplicationId,
    string ApplicationExecutable,
    string ApplicationEntryPoint)
{
    /// <summary>Validates the complete primary package identity against a canonical version.</summary>
    public void Validate(string expectedVersion)
    {
        InstallerPackageIdentityValidation.ValidateCommon(
            Name,
            Publisher,
            PublisherId,
            expectedVersion,
            Architecture,
            ResourceId,
            PackageFullName,
            PackageFamilyName,
            "installer.release.package_identity_invalid");
        if (ResourceId.Length != 0
            || !InstallerPackageIdentityValidation.IsSimpleToken(ApplicationId, 64)
            || !InstallerPackageIdentityValidation.IsCanonicalExecutable(
                ApplicationExecutable,
                128)
            || !InstallerPackageIdentityValidation.IsDottedToken(ApplicationEntryPoint, 256))
        {
            throw new InstallerProtocolException("installer.release.package_identity_invalid");
        }
    }
}

/// <summary>Exact identity of one dependency MSIX bound to a manifest file entry.</summary>
/// <param name="Path">Canonical payload-relative path of the dependency MSIX.</param>
/// <param name="Name">Package Identity Name.</param>
/// <param name="Publisher">Canonical package Publisher distinguished name.</param>
/// <param name="PublisherId">Windows-derived 13-character publisher identifier.</param>
/// <param name="Version">Canonical four-component package version.</param>
/// <param name="MinimumVersion">Exact minimum version declared by the primary package.</param>
/// <param name="Architecture">Canonical package architecture; only x64 is accepted.</param>
/// <param name="ResourceId">Exact package resource identifier.</param>
/// <param name="PackageFullName">Exact derived package full name.</param>
/// <param name="PackageFamilyName">Exact derived package family name.</param>
public sealed record InstallerDependencyPackageIdentity(
    string Path,
    string Name,
    string Publisher,
    string PublisherId,
    string Version,
    string MinimumVersion,
    string Architecture,
    string ResourceId,
    string PackageFullName,
    string PackageFamilyName)
{
    /// <summary>Validates the exact dependency identity and its canonical payload path.</summary>
    public void Validate()
    {
        var pathProbe = new InstallerPayloadFileEntry(
            Path,
            InstallerPayloadFileRole.DependencyPackage,
            Length: 1,
            Sha256: new string('0', 64));
        try
        {
            pathProbe.Validate();
            InstallerPackageIdentityValidation.ValidateCommon(
                Name,
                Publisher,
                PublisherId,
                Version,
                Architecture,
                ResourceId,
                PackageFullName,
                PackageFamilyName,
                "installer.release.dependency_identity_invalid");
            InstallerProtocolValidation.ValidatePackageVersion(MinimumVersion);
            if (ResourceId.Length != 0
                || System.Version.Parse(MinimumVersion) > System.Version.Parse(Version))
            {
                throw new InstallerProtocolException(
                    "installer.release.dependency_identity_invalid");
            }
        }
        catch (InstallerProtocolException exception)
            when (exception.DiagnosticCode != "installer.release.dependency_identity_invalid")
        {
            throw new InstallerProtocolException(
                "installer.release.dependency_identity_invalid",
                exception);
        }
    }
}

internal static class InstallerPackageIdentityValidation
{
    private const string PublisherAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    internal static void ValidateCommon(
        string name,
        string publisher,
        string publisherId,
        string version,
        string architecture,
        string resourceId,
        string packageFullName,
        string packageFamilyName,
        string diagnosticCode)
    {
        try
        {
            InstallerProtocolValidation.ValidatePackageVersion(version);
            if (!IsPackageName(name)
                || !IsPublisher(publisher)
                || publisherId is not { Length: 13 }
                || publisherId.Any(static character =>
                    PublisherAlphabet.IndexOf(character) < 0)
                || !string.Equals(publisherId, DerivePublisherId(publisher), StringComparison.Ordinal)
                || architecture != "x64"
                || !IsResourceId(resourceId)
                || !string.Equals(
                    packageFamilyName,
                    $"{name}_{publisherId}",
                    StringComparison.Ordinal)
                || !string.Equals(
                    packageFullName,
                    $"{name}_{version}_{architecture}_{resourceId}_{publisherId}",
                    StringComparison.Ordinal)
                || packageFamilyName.Length > 64
                || packageFullName.Length > 255)
            {
                throw new InstallerProtocolException(diagnosticCode);
            }
        }
        catch (InstallerProtocolException exception) when (exception.DiagnosticCode != diagnosticCode)
        {
            throw new InstallerProtocolException(diagnosticCode, exception);
        }
    }

    internal static bool IsSimpleToken(string value, int maximumLength) =>
        value is { Length: >= 1 }
        && value.Length <= maximumLength
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-');

    internal static bool IsDottedToken(string value, int maximumLength) =>
        IsSimpleToken(value, maximumLength)
        && !value.Contains("..", StringComparison.Ordinal);

    internal static bool IsCanonicalExecutable(string value, int maximumLength) =>
        value is { Length: >= 5 }
        && value.Length <= maximumLength
        && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('/')
        && !value.Contains('\\')
        && !value.Contains(':')
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static bool IsPackageName(string value) => IsDottedToken(value, 50) && value.Length >= 3;

    private static bool IsPublisher(string value) =>
        value is { Length: >= 3 and <= 512 }
        && value == value.Trim()
        && value.All(static character => character != '\0' && !char.IsControl(character));

    private static bool IsResourceId(string value) =>
        value is not null
        && value.Length <= 30
        && value.All(static character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-');

    private static string DerivePublisherId(string publisher)
    {
        byte[] publisherBytes = Encoding.Unicode.GetBytes(publisher);
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(publisherBytes, digest);
            Span<char> encoded = stackalloc char[13];
            for (int chunk = 0; chunk < encoded.Length; chunk++)
            {
                int value = 0;
                for (int offset = 0; offset < 5; offset++)
                {
                    int bitIndex = (chunk * 5) + offset;
                    int bit = bitIndex < 64
                        ? (digest[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1
                        : 0;
                    value = (value << 1) | bit;
                }

                encoded[chunk] = PublisherAlphabet[value];
            }

            return new string(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publisherBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
