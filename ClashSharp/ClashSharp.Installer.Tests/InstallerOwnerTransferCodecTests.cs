using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerOwnerTransferCodecTests
{
    [Fact]
    public void PrivateSchemaHasOneStableEncodingAndDoesNotChangeThePublicJournalFormat()
    {
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal();
        string expected = $$"""
            {"schema":1,"continuation":{"schema":2,"transactionId":"{{InstallerTestData.TransactionId}}","operation":"Install","targetSid":"{{InstallerTestData.Sid}}","allowReassociation":false,"expectedPackageVersion":"{{InstallerTestData.Version}}","installerPayloadSha256":"{{InstallerTestData.Hash}}","phase":"Prepared","generation":1},"previousOwner":{"association":{"schemaVersion":1,"ownerSid":"{{InstallerOwnerTransferJournalTests.PreviousSid}}","authenticationToken":"{{InstallerTestData.Hash}}"},"profileRoot":"C:\\Users\\previous"},"nextOwner":{"association":{"schemaVersion":1,"ownerSid":"{{InstallerTestData.Sid}}","authenticationToken":"{{InstallerTestData.OtherHash}}"},"profileRoot":"C:\\Users\\target"},"previousCertificateLedger":null,"nextCertificateLedger":null,"phase":"Prepared","generation":1}
            """;

        byte[] bytes = InstallerOwnerTransferCodec.Serialize(journal);

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(journal, InstallerOwnerTransferCodec.Parse(bytes));
        Assert.Equal(bytes, InstallerOwnerTransferCodec.Serialize(journal));
        byte[] ordinary = InstallerTransactionCodec.Serialize(journal.Continuation);
        Assert.Equal(journal.Continuation, InstallerTransactionCodec.Parse(ordinary));
        string publicJson = Encoding.UTF8.GetString(ordinary);
        Assert.DoesNotContain("authenticationToken", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("profileRoot", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallerTestData.OtherHash, publicJson, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InstallerOwnerTransferJournalTests.AllPhases), MemberType = typeof(InstallerOwnerTransferJournalTests))]
    public void EveryDurablePhasePreservesBothOwnersAndTheirCertificateEvidence(InstallerOwnerTransferPhase phase)
    {
        InstallerOwnerTransferJournal expected = InstallerOwnerTransferJournalTests.CreateJournal(withLedgers: true)
            with
        { Phase = phase, Generation = (int)phase + 1 };

        InstallerOwnerTransferJournal actual = InstallerOwnerTransferCodec.Parse(InstallerOwnerTransferCodec.Serialize(expected));

        Assert.Equal(expected, actual);
        Assert.Equal(expected.PreviousCertificateLedger, actual.PreviousCertificateLedger);
        Assert.Equal(expected.NextCertificateLedger, actual.NextCertificateLedger);
        Assert.Equal(InstallerOwnerTransferSnapshot.Create(expected), InstallerOwnerTransferSnapshot.Create(actual));
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("reordered")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("unknown")]
    [InlineData("nested-unknown")]
    [InlineData("case")]
    [InlineData("omitted-null")]
    [InlineData("numeric-phase")]
    [InlineData("numeric-operation")]
    [InlineData("escaped-property")]
    [InlineData("bom")]
    [InlineData("trailing-comma")]
    [InlineData("comment")]
    public void AlternateOrAmbiguousDocumentEncodingsAreRejected(string condition)
    {
        string canonical = Encoding.UTF8.GetString(InstallerOwnerTransferCodec.Serialize(InstallerOwnerTransferJournalTests.CreateJournal()));
        string changed = condition switch
        {
            "whitespace" => canonical.Insert(1, " "),
            "reordered" => "{\"generation\":1," + canonical[1..^16] + "}",
            "duplicate" => canonical.Replace("\"schema\":1,", "\"schema\":1,\"schema\":1,", StringComparison.Ordinal),
            "nested-duplicate" => canonical.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal),
            "unknown" => canonical.Insert(1, "\"extra\":null,"),
            "nested-unknown" => canonical.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"extra\":null,", StringComparison.Ordinal),
            "case" => canonical.Replace("\"profileRoot\"", "\"ProfileRoot\"", StringComparison.Ordinal),
            "omitted-null" => canonical.Replace("\"previousCertificateLedger\":null,", "", StringComparison.Ordinal),
            "numeric-phase" => canonical.Replace("\"phase\":\"Prepared\"", "\"phase\":0", StringComparison.Ordinal),
            "numeric-operation" => canonical.Replace("\"operation\":\"Install\"", "\"operation\":0", StringComparison.Ordinal),
            "escaped-property" => canonical.Replace("\"nextOwner\"", "\"next\\u004Fwner\"", StringComparison.Ordinal),
            "bom" => "\uFEFF" + canonical,
            "trailing-comma" => canonical[..^1] + ",}",
            "comment" => canonical.Insert(1, "/* private */"),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            () => InstallerOwnerTransferCodec.Parse(Encoding.UTF8.GetBytes(changed)));

        Assert.Equal("installer.owner_transfer.json_invalid", exception.DiagnosticCode);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("schema", "string")]
    [InlineData("continuation", "null")]
    [InlineData("previousOwner", "null")]
    [InlineData("nextOwner", "null")]
    [InlineData("previousOwner.association", "null")]
    [InlineData("previousOwner.association.ownerSid", "null")]
    [InlineData("nextOwner.association.authenticationToken", "null")]
    [InlineData("previousOwner.profileRoot", "null")]
    [InlineData("phase", "unknown")]
    [InlineData("generation", "string")]
    public void MissingPrivateIdentitiesAndWrongTypesFailAsSanitizedProtocolErrors(string path, string value)
    {
        JsonNode document = JsonNode.Parse(InstallerOwnerTransferCodec.Serialize(InstallerOwnerTransferJournalTests.CreateJournal()))!;
        string[] parts = path.Split('.');
        JsonNode parent = document;
        foreach (string part in parts[..^1])
        {
            parent = parent[part]!;
        }

        parent[parts[^1]] = value == "null" ? null : JsonValue.Create(value);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(
            () => InstallerOwnerTransferCodec.Parse(Encoding.UTF8.GetBytes(document.ToJsonString())));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(InstallerTestData.OtherHash, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\target", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[[[[[0]]]]]")]
    public void MalformedOrIncompleteRootsAreRejected(string json) =>
        Assert.Throws<InstallerProtocolException>(() => InstallerOwnerTransferCodec.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void DocumentBudgetRejectsBeforeParsingAndAllowsTheMaximumProfileFootprint()
    {
        Assert.Equal("installer.owner_transfer.document_size_invalid",
            Assert.Throws<InstallerProtocolException>(() => InstallerOwnerTransferCodec.Parse([])).DiagnosticCode);
        Assert.Equal("installer.owner_transfer.document_size_invalid",
            Assert.Throws<InstallerProtocolException>(() => InstallerOwnerTransferCodec.Parse(new byte[InstallerOwnerTransferCodec.MaximumDocumentBytes + 1])).DiagnosticCode);
        InstallerOwnerTransferJournal journal = InstallerOwnerTransferJournalTests.CreateJournal(withLedgers: true);
        journal = journal with
        {
            PreviousOwner = journal.PreviousOwner with { ProfileRoot = @"C:\" + new string('旧', 1021) },
            NextOwner = journal.NextOwner with { ProfileRoot = @"C:\" + new string('新', 1021) },
        };

        byte[] bytes = InstallerOwnerTransferCodec.Serialize(journal);

        Assert.InRange(bytes.Length, 1, InstallerOwnerTransferCodec.MaximumDocumentBytes);
        Assert.Equal(journal, InstallerOwnerTransferCodec.Parse(bytes));
    }
}
