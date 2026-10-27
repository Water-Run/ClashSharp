using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerServiceProfileTests
{
    [Fact]
    public void LegacyServiceProfileKeepsTheOriginalNineFieldCanonicalDocument()
    {
        InstallerTransactionJournal journal = InstallerTestData.Journal();
        string expected = "{\"schema\":2,\"transactionId\":\"" + InstallerTestData.TransactionId
            + "\",\"operation\":\"install\",\"targetSid\":\"" + InstallerTestData.Sid
            + "\",\"allowReassociation\":false,\"expectedPackageVersion\":\"" + InstallerTestData.Version
            + "\",\"installerPayloadSha256\":\"" + InstallerTestData.Hash + "\",\"phase\":\"prepared\",\"generation\":1}";
        byte[] bytes = InstallerTransactionCodec.Serialize(journal);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.True(InstallerTransactionCodec.Parse(bytes).InstallTransparentProxyService);
    }

    [Fact]
    public void UserOnlyProfileIsBoundToTheJournalAndSurvivesHelperTransportAndPhaseAdvance()
    {
        InstallerRequest request = InstallerTestData.Request() with { InstallTransparentProxyService = false };
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        byte[] bytes = InstallerTransactionCodec.Serialize(journal);
        Assert.False(InstallerTransactionCodec.Parse(bytes).InstallTransparentProxyService);
        Assert.True(journal.Matches(request));
        Assert.False(journal.Matches(request with { InstallTransparentProxyService = true }));
        Assert.False(journal.TransitionTo(InstallerTransactionPhase.MachineReserved).InstallTransparentProxyService);
        InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
        var invocation = new InstallerMachineHelperInvocation(InstallerMachineHelperVerb.Prepare, journal.TransactionId, snapshot.ContentHash);
        InstallerMachineHelperCommand command = InstallerMachineHelperCommand.Create(invocation, snapshot);
        Assert.False(InstallerMachineHelperCommandCodec.Parse(InstallerMachineHelperCommandCodec.Serialize(command))
            .ToDurableState().Journal.InstallTransparentProxyService);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(bytes)),
            Convert.ToHexString(SHA256.HashData(InstallerTransactionCodec.Serialize(journal with { InstallTransparentProxyService = true }))));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"false\"")]
    public void NoncanonicalOrUntypedServiceProfileIsRejected(string value)
    {
        string legacy = Encoding.UTF8.GetString(InstallerTransactionCodec.Serialize(InstallerTestData.Journal()));
        string changed = legacy[..^1] + ",\"installTransparentProxyService\":" + value + '}';
        Assert.Throws<InstallerProtocolException>(() => InstallerTransactionCodec.Parse(Encoding.UTF8.GetBytes(changed)));
    }

    [Fact]
    public void UserOnlyProfileCannotGrantSharedServiceReassociation()
    {
        InstallerRequest request = InstallerTestData.Request(InstallerOperation.Repair, allowReassociation: true)
            with
        { InstallTransparentProxyService = false };
        Assert.Equal("installer.request.service_profile_conflict", Assert.Throws<InstallerProtocolException>(request.Validate).DiagnosticCode);
    }
}
