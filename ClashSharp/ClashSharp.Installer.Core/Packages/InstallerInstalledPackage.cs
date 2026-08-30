using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Packages;

/// <summary>Exact identity of one package registration observed for the target user.</summary>
/// <param name="Name">Registered package identity name.</param>
/// <param name="Publisher">Registered package publisher distinguished name.</param>
/// <param name="PublisherId">Registered package publisher identifier.</param>
/// <param name="Version">Registered canonical four-component version.</param>
/// <param name="Architecture">Registered package architecture.</param>
/// <param name="ResourceId">Registered package resource identifier.</param>
/// <param name="PackageFullName">Registered package full name.</param>
/// <param name="PackageFamilyName">Registered package family name.</param>
/// <param name="IsHealthy">Whether Windows reports no servicing, integrity, or availability issue.</param>
public sealed record InstallerInstalledPackage(
    string Name,
    string Publisher,
    string PublisherId,
    string Version,
    string Architecture,
    string ResourceId,
    string PackageFullName,
    string PackageFamilyName,
    bool IsHealthy)
{
    /// <summary>Validates the complete derived registration identity.</summary>
    public void Validate()
    {
        try
        {
            InstallerPackageIdentityValidation.ValidateCommon(
                Name,
                Publisher,
                PublisherId,
                Version,
                Architecture,
                ResourceId,
                PackageFullName,
                PackageFamilyName,
                "installer.package.installed_identity_invalid");
        }
        catch (ArgumentException exception)
        {
            throw new InstallerProtocolException(
                "installer.package.installed_identity_invalid",
                exception);
        }
    }

    /// <summary>Checks every stable identity field against a validated release manifest.</summary>
    public bool MatchesReleaseFamily(InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        Validate();
        InstallerPackageIdentity expected = manifest.PackageIdentity;
        return string.Equals(Name, expected.Name, StringComparison.Ordinal)
            && string.Equals(Publisher, expected.Publisher, StringComparison.Ordinal)
            && string.Equals(PublisherId, expected.PublisherId, StringComparison.Ordinal)
            && string.Equals(Architecture, expected.Architecture, StringComparison.Ordinal)
            && string.Equals(ResourceId, expected.ResourceId, StringComparison.Ordinal)
            && string.Equals(
                PackageFullName,
                $"{expected.Name}_{Version}_{expected.Architecture}_{expected.ResourceId}_{expected.PublisherId}",
                StringComparison.Ordinal)
            && string.Equals(
                PackageFamilyName,
                expected.PackageFamilyName,
                StringComparison.Ordinal);
    }
}
