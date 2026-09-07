using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerMachineCertificateOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalEvidenceRoundTripsWithAnIndependentMachineScope(bool preExisting)
    {
        var ownership = InstallerMachineCertificateOwnership.Create(InstallerTestData.Manifest(), preExisting);
        byte[] bytes = ownership.Serialize();
        Assert.Equal(ownership, InstallerMachineCertificateOwnership.Parse(bytes));
        Assert.Equal(!preExisting, ownership.InstallerOwned);
        Assert.Equal("localMachine", ownership.StoreLocation);
        Assert.Equal("trustedPeople", ownership.StoreName);
        Assert.DoesNotContain("targetSid", Encoding.UTF8.GetString(bytes));
        Assert.Throws<InstallerProtocolException>(() => InstallerCertificateOwnershipCodec.Parse(bytes));
    }

    [Theory]
    [InlineData("\"schema\":1", "\"schema\":2")]
    [InlineData("\"schema\":1", "\"schema\":\"1\"")]
    [InlineData("\"schema\":1", "\"schema\":1,\"schema\":1")]
    [InlineData("\"schema\":1,", "")]
    [InlineData("\"schema\":1", "\"Schema\":1")]
    [InlineData("localMachine", "currentUser")]
    [InlineData("trustedPeople", "root")]
    [InlineData("\"installerOwned\":true", "\"installerOwned\":1")]
    [InlineData("\"installerOwned\":true", "\"installerOwned\":\"true\"")]
    [InlineData("\"packageName\":", "\"unknown\":0,\"packageName\":")]
    [InlineData("CN=linzh", "CN=another")]
    [InlineData("vj7sjtzkt239a", "0000000000000")]
    [InlineData("\"publisher\":\"CN=linzh\"", "\"publisher\":null")]
    [InlineData("\"publisher\":\"CN=linzh\"", "\"publisher\":[\"CN=linzh\"]")]
    public void UntrustedDocumentShapesAndScopesAreRejected(string original, string replacement)
    {
        string json = Encoding.UTF8.GetString(InstallerMachineCertificateOwnership.Create(InstallerTestData.Manifest(), false).Serialize());
        Assert.Contains(original, json);
        byte[] changed = Encoding.UTF8.GetBytes(json.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<InstallerProtocolException>(() => InstallerMachineCertificateOwnership.Parse(changed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    public void DocumentSizeIsBounded(int length) => Assert.Throws<InstallerProtocolException>(
        () => InstallerMachineCertificateOwnership.Parse(new byte[length]));

    [Fact]
    public void UserOwnershipEvidenceCannotBeInterpretedAsMachineOwnership() => Assert.Throws<InstallerProtocolException>(
        () => InstallerMachineCertificateOwnership.Parse(InstallerCertificateOwnershipCodec.Serialize(InstallerTestData.CertificateLedger())));

    [Fact]
    public void AnotherReleaseCertificateCannotReuseMachineOwnership()
    {
        var ownership = InstallerMachineCertificateOwnership.Create(InstallerTestData.Manifest(), false);
        Assert.Throws<InstallerProtocolException>(() => ownership.RequireManifest(InstallerTestData.Manifest(
            InstallerTestData.Release(certificateHash: InstallerTestData.OtherHash))));
    }
}
