using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerCertificateOwnershipLedgerTests
{
    [Fact]
    public void CreateRecordsAPreExistingCertificateWithoutClaimingOwnership()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerCertificateOwnershipLedger.Create(
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            certificateWasPresent: true);

        Assert.True(ledger.WasPreExisting);
        Assert.False(ledger.InstallerOwned);
        Assert.Equal(1, ledger.ManagedReferenceCount);
        Assert.Equal(1, ledger.Generation);
        Assert.Equal(InstallerTestData.Sid, ledger.TargetSid);
        Assert.Equal(InstallerTestData.CertificateThumbprint, ledger.CertificateThumbprint);
        ledger.Validate();
    }

    [Fact]
    public void CreateClaimsAMissingCertificateBeforeImport()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerCertificateOwnershipLedger.Create(
            InstallerTestData.Request(),
            InstallerTestData.Release(),
            certificateWasPresent: false);

        Assert.False(ledger.WasPreExisting);
        Assert.True(ledger.InstallerOwned);
        Assert.Equal(1, ledger.ManagedReferenceCount);
        ledger.Validate();
    }

    [Fact]
    public void MissingCertificateCannotBeClaimedWithoutVerifiedPayload()
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerCertificateOwnershipLedger.Create(
                InstallerTestData.Request(),
                InstallerTestData.Release(certificatePayloadAvailable: false),
                certificateWasPresent: false));

        Assert.Equal("installer.release.certificate_payload_missing", exception.DiagnosticCode);
    }

    [Fact]
    public void UninstallCannotCreateNewOwnershipEvidence()
    {
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            InstallerCertificateOwnershipLedger.Create(
                InstallerTestData.Request(InstallerOperation.Uninstall),
                InstallerTestData.Release(),
                certificateWasPresent: false));

        Assert.Equal("installer.certificate.create_for_uninstall_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void MissingPreExistingCertificateTransitionsToOwnedBeforeImport()
    {
        InstallerCertificateOwnershipLedger original = InstallerTestData.CertificateLedger(
            wasPreExisting: true);
        InstallerCertificateOwnershipLedger owned = original.TakeOwnershipForMissingCertificate();

        Assert.False(owned.WasPreExisting);
        Assert.True(owned.InstallerOwned);
        Assert.Equal(2, owned.Generation);
        Assert.Same(owned, owned.TakeOwnershipForMissingCertificate());
    }

    [Theory]
    [InlineData(false, 1, 1, 2)]
    [InlineData(true, 1, 1, 2)]
    [InlineData(false, 1, 2, 3)]
    public void RemovalPreparationIsMonotonicAndIdempotent(
        bool wasPreExisting,
        int references,
        int generation,
        int expectedGeneration)
    {
        InstallerCertificateOwnershipLedger original = InstallerTestData.CertificateLedger(
            wasPreExisting,
            references,
            generation);
        InstallerCertificateOwnershipLedger unreferenced = original.PrepareRemoval();

        Assert.Equal(0, unreferenced.ManagedReferenceCount);
        Assert.Equal(expectedGeneration, unreferenced.Generation);
        Assert.Same(unreferenced, unreferenced.PrepareRemoval());
    }

    [Fact]
    public void MatchesBindsSidThumbprintAndFullCertificateHash()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();

        Assert.True(ledger.Matches(InstallerTestData.Request(), InstallerTestData.Release()));
        Assert.False(ledger.Matches(
            new InstallerRequest(
                InstallerOperation.Install,
                "S-1-5-21-100-200-300-1002",
                AllowReassociation: false,
                InstallerTestData.Version,
                InstallerTestData.Hash),
            InstallerTestData.Release()));
        Assert.False(ledger.Matches(
            InstallerTestData.Request(),
            InstallerTestData.Release(certificateHash: InstallerTestData.OtherHash)));
    }

    [Theory]
    [InlineData(true, true, 1, 1, "installer.certificate.ownership_invalid")]
    [InlineData(false, false, 1, 1, "installer.certificate.ownership_invalid")]
    [InlineData(false, true, 2, 1, "installer.certificate.ownership_invalid")]
    [InlineData(true, false, 1, 2, "installer.certificate.generation_invalid")]
    [InlineData(false, true, 1, 3, "installer.certificate.generation_invalid")]
    public void InvalidOwnershipOrGenerationCombinationsFailClosed(
        bool wasPreExisting,
        bool installerOwned,
        int references,
        int generation,
        string diagnosticCode)
    {
        InstallerCertificateOwnershipLedger invalid = InstallerTestData.CertificateLedger() with
        {
            WasPreExisting = wasPreExisting,
            InstallerOwned = installerOwned,
            ManagedReferenceCount = references,
            Generation = generation,
        };

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(invalid.Validate);
        Assert.Equal(diagnosticCode, exception.DiagnosticCode);
    }

    [Fact]
    public void SchemaAndStoreAreExactProtocolValues()
    {
        InstallerCertificateOwnershipLedger valid = InstallerTestData.CertificateLedger();
        InstallerProtocolException schema = Assert.Throws<InstallerProtocolException>(
            () => (valid with { Schema = 2 }).Validate());
        InstallerProtocolException location = Assert.Throws<InstallerProtocolException>(
            () => (valid with { StoreLocation = (InstallerCertificateStoreLocation)99 }).Validate());
        InstallerProtocolException store = Assert.Throws<InstallerProtocolException>(
            () => (valid with { StoreName = (InstallerCertificateStoreName)99 }).Validate());

        Assert.Equal("installer.certificate.ledger_schema_invalid", schema.DiagnosticCode);
        Assert.Equal("installer.certificate.store_invalid", location.DiagnosticCode);
        Assert.Equal("installer.certificate.store_invalid", store.DiagnosticCode);
    }
}
