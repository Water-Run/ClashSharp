using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerTransactionCodecTests
{
    [Fact]
    public void CanonicalDocumentRoundTrips()
    {
        InstallerTransactionJournal expected = InstallerTestData.Journal();
        byte[] bytes = InstallerTransactionCodec.Serialize(expected);

        Assert.Equal(expected, InstallerTransactionCodec.Parse(bytes));
        Assert.StartsWith("{\"schema\":2,\"transactionId\":", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\":2,}")]
    [InlineData("{/*comment*/\"schema\":2}")]
    public void IncompleteOrNoncanonicalJsonIsRejected(string json) =>
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerTransactionCodec.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UnknownPropertyIsRejected()
    {
        string json = CanonicalJson().Replace(
            "\"generation\":1",
            "\"generation\":1,\"unexpected\":true",
            StringComparison.Ordinal);
        AssertJsonInvalid(json);
    }

    [Fact]
    public void DuplicatePropertyIsRejectedBeforeDeserialization()
    {
        string json = CanonicalJson().Replace(
            "\"schema\":2",
            "\"schema\":2,\"schema\":2",
            StringComparison.Ordinal);
        AssertJsonInvalid(json);
    }

    [Fact]
    public void PropertyNamesAreCaseSensitive()
    {
        AssertJsonInvalid(CanonicalJson().Replace("\"schema\"", "\"Schema\"", StringComparison.Ordinal));
    }

    [Fact]
    public void WhitespaceAndReorderedPropertiesAreRejectedAsNoncanonicalBytes()
    {
        string canonical = CanonicalJson();
        AssertJsonInvalid(canonical.Insert(1, " "));
        AssertJsonInvalid(canonical.Replace(
            $"{{\"schema\":2,\"transactionId\":\"{InstallerTestData.TransactionId}\",",
            $"{{\"transactionId\":\"{InstallerTestData.TransactionId}\",\"schema\":2,",
            StringComparison.Ordinal));
    }

    [Fact]
    public void IntegerEnumsAreRejected()
    {
        AssertJsonInvalid(CanonicalJson().Replace("\"operation\":\"install\"", "\"operation\":0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"schema\":\"2\"")]
    [InlineData("\"transactionId\":null")]
    [InlineData("\"operation\":null")]
    [InlineData("\"targetSid\":null")]
    [InlineData("\"allowReassociation\":\"false\"")]
    [InlineData("\"expectedPackageVersion\":null")]
    [InlineData("\"installerPayloadSha256\":null")]
    [InlineData("\"generation\":\"1\"")]
    [InlineData("\"phase\":null")]
    public void PropertyValueTypesAreStrict(string replacement)
    {
        string propertyName = replacement[1..replacement.IndexOf('\"', 1)];
        string original = propertyName switch
        {
            "schema" => "\"schema\":2",
            "transactionId" => $"\"transactionId\":\"{InstallerTestData.TransactionId}\"",
            "operation" => "\"operation\":\"install\"",
            "targetSid" => $"\"targetSid\":\"{InstallerTestData.Sid}\"",
            "allowReassociation" => "\"allowReassociation\":false",
            "expectedPackageVersion" => $"\"expectedPackageVersion\":\"{InstallerTestData.Version}\"",
            "installerPayloadSha256" => $"\"installerPayloadSha256\":\"{InstallerTestData.Hash}\"",
            "phase" => "\"phase\":\"prepared\"",
            "generation" => "\"generation\":1",
            _ => throw new InvalidOperationException("Unknown test property."),
        };
        AssertJsonInvalid(CanonicalJson().Replace(original, replacement, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyAndOversizedDocumentsAreRejected()
    {
        Assert.Throws<InstallerProtocolException>(() => InstallerTransactionCodec.Parse([]));
        byte[] bytes = new byte[InstallerTransactionCodec.MaximumDocumentBytes + 1];
        Assert.Throws<InstallerProtocolException>(() => InstallerTransactionCodec.Parse(bytes));
    }

    private static string CanonicalJson() =>
        Encoding.UTF8.GetString(InstallerTransactionCodec.Serialize(InstallerTestData.Journal()));

    private static void AssertJsonInvalid(string json)
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerTransactionCodec.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("installer.transaction.json_invalid", exception.DiagnosticCode);
    }
}
