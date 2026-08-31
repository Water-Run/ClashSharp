using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerPackageIdentityTests
{
    [Fact]
    public void CanonicalPrimaryAndMicrosoftDependencyIdentitiesAreAccepted()
    {
        InstallerPackageIdentity primary = InstallerTestData.PackageIdentity();
        InstallerDependencyPackageIdentity dependency = InstallerTestData.DependencyIdentity();

        primary.Validate(InstallerTestData.Version);
        dependency.Validate();

        Assert.Equal("vj7sjtzkt239a", primary.PublisherId);
        Assert.Equal("8wekyb3d8bbwe", dependency.PublisherId);
    }

    [Theory]
    [InlineData("publisher_id")]
    [InlineData("family")]
    [InlineData("full_name")]
    [InlineData("architecture")]
    [InlineData("resource_id")]
    public void PrimaryDerivedIdentityMismatchIsRejected(string invalidCase)
    {
        InstallerPackageIdentity identity = invalidCase switch
        {
            "publisher_id" => InstallerTestData.PackageIdentity() with
            {
                PublisherId = "0000000000000",
            },
            "family" => InstallerTestData.PackageIdentity() with
            {
                PackageFamilyName = "wrong_family",
            },
            "full_name" => InstallerTestData.PackageIdentity() with
            {
                PackageFullName = "wrong_full_name",
            },
            "architecture" => InstallerTestData.PackageIdentity() with
            {
                Architecture = "arm64",
            },
            "resource_id" => InstallerTestData.PackageIdentity() with
            {
                ResourceId = "resources",
                PackageFullName = $"{InstallerTestData.PackageName}_{InstallerTestData.Version}_x64_resources_{InstallerTestData.PackagePublisherId}",
            },
            _ => throw new InvalidOperationException(),
        };

        AssertDiagnostic(
            () => identity.Validate(InstallerTestData.Version),
            "installer.release.package_identity_invalid");
    }

    [Theory]
    [InlineData("../ClashSharp.exe", "Windows.FullTrustApplication")]
    [InlineData("ClashSharp.dll", "Windows.FullTrustApplication")]
    [InlineData("ClashSharp.exe", "Windows..FullTrustApplication")]
    [InlineData("ClashSharp.exe", "Windows/FullTrustApplication")]
    public void PrimaryApplicationIdentityIsCanonical(
        string executable,
        string entryPoint)
    {
        InstallerPackageIdentity identity = InstallerTestData.PackageIdentity() with
        {
            ApplicationExecutable = executable,
            ApplicationEntryPoint = entryPoint,
        };

        AssertDiagnostic(
            () => identity.Validate(InstallerTestData.Version),
            "installer.release.package_identity_invalid");
    }

    [Theory]
    [InlineData("01.2.3.4")]
    [InlineData("8000.900.1.65536")]
    public void DependencyVersionIsCanonical(string version)
    {
        InstallerDependencyPackageIdentity identity = InstallerTestData.DependencyIdentity(
            version: version) with
        {
            PackageFullName = $"{InstallerTestData.DependencyName}_{version}_x64__{InstallerTestData.DependencyPublisherId}",
        };

        AssertDiagnostic(
            identity.Validate,
            "installer.release.dependency_identity_invalid");
    }

    [Fact]
    public void DependencyMinimumVersionCannotExceedPayloadVersion()
    {
        InstallerDependencyPackageIdentity identity = InstallerTestData.DependencyIdentity() with
        {
            MinimumVersion = "8000.901.0.0",
        };

        AssertDiagnostic(
            identity.Validate,
            "installer.release.dependency_identity_invalid");
    }

    [Fact]
    public void DependencyIdentitySetMustExactlyMatchDependencyFiles()
    {
        InstallerReleaseManifest canonical = InstallerTestData.Manifest();
        InstallerReleaseManifest wrongPath = CopyManifest(
            canonical,
            [InstallerTestData.DependencyIdentity("dependencies/x64/other.msix")]);
        AssertDiagnostic(
            wrongPath.Validate,
            "installer.release.dependency_identity_set_invalid");

        InstallerReleaseManifest missing = CopyManifest(canonical, []);
        AssertDiagnostic(
            missing.Validate,
            "installer.release.dependency_identity_set_invalid");
    }

    [Fact]
    public void DuplicateDependencyFamilyIsRejected()
    {
        InstallerReleaseManifest canonical = InstallerTestData.Manifest();
        InstallerPayloadFileEntry extraFile = new(
            "dependencies/x64/duplicate.msix",
            InstallerPayloadFileRole.DependencyPackage,
            1,
            InstallerTestData.OtherHash);
        InstallerPayloadFileEntry[] files = canonical.Files
            .Append(extraFile)
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        InstallerDependencyPackageIdentity[] dependencies =
        [
            InstallerTestData.DependencyIdentity("dependencies/x64/duplicate.msix"),
            InstallerTestData.DependencyIdentity(),
        ];
        InstallerReleaseManifest duplicate = CopyManifest(canonical, dependencies, files);

        AssertDiagnostic(
            duplicate.Validate,
            "installer.release.dependency_identity_set_invalid");
    }

    private static InstallerReleaseManifest CopyManifest(
        InstallerReleaseManifest source,
        IReadOnlyList<InstallerDependencyPackageIdentity> dependencies,
        IReadOnlyList<InstallerPayloadFileEntry>? files = null) =>
        new(
            source.Schema,
            source.ExpectedPackageVersion,
            source.InstallerPayloadSha256,
            source.AuthenticodeCertificateThumbprint,
            source.PackageCertificateThumbprint,
            source.CertificateSha256,
            source.PackageIdentity,
            dependencies,
            source.MachineFiles,
            files ?? source.Files);

    private static void AssertDiagnostic(Action action, string expectedCode)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }
}
