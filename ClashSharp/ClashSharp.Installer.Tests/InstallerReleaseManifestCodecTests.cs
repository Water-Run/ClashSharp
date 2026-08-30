using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerReleaseManifestCodecTests
{
    [Fact]
    public void CanonicalManifestRoundTripsAsCompactJson()
    {
        InstallerReleaseManifest expected = InstallerTestData.Manifest();
        byte[] bytes = InstallerReleaseManifestCodec.Serialize(expected);
        InstallerReleaseManifest actual = InstallerReleaseManifestCodec.Parse(bytes);

        Assert.StartsWith("{\"schema\":1,\"expectedPackageVersion\":", Encoding.UTF8.GetString(bytes));
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.ExpectedPackageVersion, actual.ExpectedPackageVersion);
        Assert.Equal(expected.InstallerPayloadSha256, actual.InstallerPayloadSha256);
        Assert.Equal(expected.PackageCertificateThumbprint, actual.PackageCertificateThumbprint);
        Assert.Equal(expected.CertificateSha256, actual.CertificateSha256);
        Assert.Equal(expected.PackageIdentity, actual.PackageIdentity);
        Assert.Equal(expected.Dependencies.ToArray(), actual.Dependencies.ToArray());
        Assert.Equal(expected.MachineFiles.ToArray(), actual.MachineFiles.ToArray());
        Assert.Equal(expected.Files.ToArray(), actual.Files.ToArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("{/*comment*/\"schema\":1}")]
    public void IncompleteOrNoncanonicalJsonIsRejected(string json) => AssertJsonInvalid(json);

    [Fact]
    public void UnknownDuplicateAndCaseChangedManifestPropertiesAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"files\":[",
            "\"unexpected\":true,\"files\":[",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"schema\":1",
            "\"schema\":1,\"schema\":1",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"schema\"",
            "\"Schema\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDuplicateAndCaseChangedFilePropertiesAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"length\":256",
            "\"length\":256,\"unexpected\":true",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"path\":\"clashsharp_1.2.3.4_x64.msix\"",
            "\"path\":\"clashsharp_1.2.3.4_x64.msix\",\"path\":\"other.msix\"",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"sha256\"",
            "\"Sha256\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDuplicateAndCaseChangedMachineFilePropertiesAreRejected()
    {
        const string path = "\"path\":\"binaries/geodata/asn.mmdb\"";
        AssertJsonInvalid(CanonicalJson().Replace(
            path,
            $"{path},\"unexpected\":true",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            path,
            $"{path},{path}",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            path,
            "\"Path\":\"binaries/geodata/asn.mmdb\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDuplicateAndCaseChangedIdentityPropertiesAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"applicationId\":\"App\"",
            "\"applicationId\":\"App\",\"unexpected\":true",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"publisherId\":\"vj7sjtzkt239a\"",
            "\"publisherId\":\"vj7sjtzkt239a\",\"publisherId\":\"vj7sjtzkt239a\"",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"applicationEntryPoint\"",
            "\"ApplicationEntryPoint\"",
            StringComparison.Ordinal));

        AssertJsonInvalid(CanonicalJson().Replace(
            "\"version\":\"8000.900.1.0\"",
            "\"version\":\"8000.900.1.0\",\"unexpected\":true",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"path\":\"dependencies/x64/microsoft.windowsappruntime.1.8.msix\"",
            "\"path\":\"dependencies/x64/microsoft.windowsappruntime.1.8.msix\",\"path\":\"other.msix\"",
            StringComparison.Ordinal));
    }

    [Fact]
    public void IntegerEnumsAndFloatingPointLengthsAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"role\":\"primaryPackage\"",
            "\"role\":0",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"length\":256",
            "\"length\":256.0",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"schema\":\"1\"")]
    [InlineData("\"expectedPackageVersion\":null")]
    [InlineData("\"installerPayloadSha256\":null")]
    [InlineData("\"packageCertificateThumbprint\":null")]
    [InlineData("\"certificateSha256\":null")]
    public void ManifestPropertyValueTypesAreStrict(string replacement)
    {
        string propertyName = replacement[1..replacement.IndexOf('"', 1)];
        string original = propertyName switch
        {
            "schema" => "\"schema\":1",
            "expectedPackageVersion" => $"\"expectedPackageVersion\":\"{InstallerTestData.Version}\"",
            "installerPayloadSha256" => $"\"installerPayloadSha256\":\"{InstallerTestData.Hash}\"",
            "packageCertificateThumbprint" => $"\"packageCertificateThumbprint\":\"{InstallerTestData.CertificateThumbprint}\"",
            "certificateSha256" => $"\"certificateSha256\":\"{InstallerTestData.CertificateHash}\"",
            _ => throw new InvalidOperationException("Unknown manifest property."),
        };
        string json = CanonicalJson().Replace(original, replacement, StringComparison.Ordinal);
        AssertJsonInvalid(json);
    }

    [Theory]
    [InlineData("packageIdentity")]
    [InlineData("dependencies")]
    [InlineData("machineFiles")]
    [InlineData("files")]
    public void NestedObjectAndArrayTypesAreStrict(string propertyName)
    {
        JsonObject root = JsonNode.Parse(CanonicalJson())!.AsObject();
        root[propertyName] = null;

        AssertJsonInvalid(root.ToJsonString());
    }

    [Fact]
    public void PackageAndDependencyIdentityValueTypesAreStrict()
    {
        JsonObject primaryRoot = JsonNode.Parse(CanonicalJson())!.AsObject();
        primaryRoot["packageIdentity"]!["name"] = null;
        AssertJsonInvalid(primaryRoot.ToJsonString());

        JsonObject dependencyRoot = JsonNode.Parse(CanonicalJson())!.AsObject();
        dependencyRoot["dependencies"]![0]!["version"] = 1;
        AssertJsonInvalid(dependencyRoot.ToJsonString());
    }

    [Theory]
    [InlineData("\"path\":null")]
    [InlineData("\"role\":null")]
    [InlineData("\"length\":\"256\"")]
    [InlineData("\"sha256\":null")]
    public void FilePropertyValueTypesAreStrict(string replacement)
    {
        string propertyName = replacement[1..replacement.IndexOf('"', 1)];
        string original = propertyName switch
        {
            "path" => "\"path\":\"clashsharp_1.2.3.4_x64.msix\"",
            "role" => "\"role\":\"primaryPackage\"",
            "length" => "\"length\":256",
            "sha256" => $"\"sha256\":\"{InstallerTestData.Hash}\"",
            _ => throw new InvalidOperationException("Unknown file property."),
        };
        AssertJsonInvalid(CanonicalJson().Replace(original, replacement, StringComparison.Ordinal));
    }

    [Fact]
    public void MachineFilePropertyValueTypesAreStrict()
    {
        JsonObject nullPath = JsonNode.Parse(CanonicalJson())!.AsObject();
        nullPath["machineFiles"]![0]!["path"] = null;
        AssertJsonInvalid(nullPath.ToJsonString());

        JsonObject stringLength = JsonNode.Parse(CanonicalJson())!.AsObject();
        stringLength["machineFiles"]![0]!["length"] = "1";
        AssertJsonInvalid(stringLength.ToJsonString());

        JsonObject nullHash = JsonNode.Parse(CanonicalJson())!.AsObject();
        nullHash["machineFiles"]![0]!["sha256"] = null;
        AssertJsonInvalid(nullHash.ToJsonString());
    }

    [Fact]
    public void EmptyAndOversizedDocumentsAreRejectedBeforeParsing()
    {
        AssertSizeInvalid([]);
        AssertSizeInvalid(new byte[InstallerPayloadBudgets.MaximumManifestBytes + 1]);
    }

    private static string CanonicalJson() => Encoding.UTF8.GetString(
        InstallerReleaseManifestCodec.Serialize(InstallerTestData.Manifest()));

    private static void AssertJsonInvalid(string json)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerReleaseManifestCodec.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("installer.release.manifest_json_invalid", exception.DiagnosticCode);
    }

    private static void AssertSizeInvalid(byte[] bytes)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerReleaseManifestCodec.Parse(bytes));
        Assert.Equal("installer.release.manifest_size_invalid", exception.DiagnosticCode);
    }
}
