using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerCertificateOwnershipCodecTests
{
    [Fact]
    public void CanonicalDocumentRoundTrips()
    {
        InstallerCertificateOwnershipLedger expected = InstallerTestData.CertificateLedger();
        byte[] bytes = InstallerCertificateOwnershipCodec.Serialize(expected);

        Assert.Equal(expected, InstallerCertificateOwnershipCodec.Parse(bytes));
        Assert.StartsWith("{\"schema\":1,\"ledgerId\":", Encoding.UTF8.GetString(bytes));
        Assert.Contains("\"storeLocation\":\"currentUser\"", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("{/*comment*/\"schema\":1}")]
    public void IncompleteOrMalformedJsonIsRejected(string json) =>
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerCertificateOwnershipCodec.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UnknownDuplicateAndWrongCasePropertiesAreRejected()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Replace(
            "\"generation\":1",
            "\"generation\":1,\"unexpected\":true",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace(
            "\"schema\":1",
            "\"schema\":1,\"schema\":1",
            StringComparison.Ordinal));
        AssertJsonInvalid(canonical.Replace("\"ledgerId\"", "\"LedgerId\"", StringComparison.Ordinal));
    }

    [Fact]
    public void IntegerEnumsAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"storeLocation\":\"currentUser\"",
            "\"storeLocation\":0",
            StringComparison.Ordinal));
        AssertJsonInvalid(CanonicalJson().Replace(
            "\"storeName\":\"trustedPeople\"",
            "\"storeName\":0",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"schema\":\"1\"")]
    [InlineData("\"ledgerId\":null")]
    [InlineData("\"targetSid\":null")]
    [InlineData("\"certificateThumbprint\":null")]
    [InlineData("\"certificateSha256\":null")]
    [InlineData("\"storeLocation\":null")]
    [InlineData("\"storeName\":null")]
    [InlineData("\"wasPreExisting\":\"false\"")]
    [InlineData("\"installerOwned\":\"true\"")]
    [InlineData("\"managedReferenceCount\":\"1\"")]
    [InlineData("\"generation\":\"1\"")]
    public void PropertyValueTypesAreStrict(string replacement)
    {
        string propertyName = replacement[1..replacement.IndexOf('"', 1)];
        string original = propertyName switch
        {
            "schema" => "\"schema\":1",
            "ledgerId" => $"\"ledgerId\":\"{InstallerTestData.TransactionId}\"",
            "targetSid" => $"\"targetSid\":\"{InstallerTestData.Sid}\"",
            "certificateThumbprint" =>
                $"\"certificateThumbprint\":\"{InstallerTestData.CertificateThumbprint}\"",
            "certificateSha256" =>
                $"\"certificateSha256\":\"{InstallerTestData.CertificateHash}\"",
            "storeLocation" => "\"storeLocation\":\"currentUser\"",
            "storeName" => "\"storeName\":\"trustedPeople\"",
            "wasPreExisting" => "\"wasPreExisting\":false",
            "installerOwned" => "\"installerOwned\":true",
            "managedReferenceCount" => "\"managedReferenceCount\":1",
            "generation" => "\"generation\":1",
            _ => throw new InvalidOperationException("Unknown test property."),
        };
        AssertJsonInvalid(CanonicalJson().Replace(original, replacement, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyAndOversizedDocumentsAreRejected()
    {
        Assert.Throws<InstallerProtocolException>(() => InstallerCertificateOwnershipCodec.Parse([]));
        byte[] bytes = new byte[InstallerCertificateOwnershipCodec.MaximumDocumentBytes + 1];
        Assert.Throws<InstallerProtocolException>(() => InstallerCertificateOwnershipCodec.Parse(bytes));
    }

    private static string CanonicalJson() => Encoding.UTF8.GetString(
        InstallerCertificateOwnershipCodec.Serialize(InstallerTestData.CertificateLedger()));

    private static void AssertJsonInvalid(string json)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerCertificateOwnershipCodec.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("installer.certificate.json_invalid", exception.DiagnosticCode);
    }
}
