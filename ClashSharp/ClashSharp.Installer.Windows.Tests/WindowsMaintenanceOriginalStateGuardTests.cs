using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenanceOriginalStateGuardTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Credential = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CompletePrivateBaselineRoundTripsWithoutLosingAnyEvidenceCategory()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var baseline = new WindowsMaintenanceOriginalBaseline(WindowsMaintenanceOriginalBaseline.CurrentSchema, Baseline(plan), Contents(plan));
        byte[] bytes = baseline.Serialize();
        try
        {
            WindowsMaintenanceOriginalBaseline read = WindowsMaintenanceOriginalBaseline.Parse(bytes);
            read.RequireBoundary(plan, read.Service.Intent.TransitionTo(InstallerTransactionPhase.MachineReserved));
            Assert.True(baseline.Contents.Matches(read.Contents));
            Assert.Equal(bytes, read.Serialize());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void CompletePrivateBaselineRejectsAlteredDocumentShape(string scenario)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        byte[] original = new WindowsMaintenanceOriginalBaseline(WindowsMaintenanceOriginalBaseline.CurrentSchema, Baseline(plan), Contents(plan)).Serialize();
        string json = Encoding.UTF8.GetString(original);
        string altered = scenario switch
        {
            "unknown" => json.Insert(1, "\"extra\":true,"),
            "duplicate" => json.Insert(1, "\"schema\":1,"),
            _ => json.Replace("\"schema\":1,", string.Empty, StringComparison.Ordinal),
        };
        byte[] bytes = Encoding.UTF8.GetBytes(altered);
        try { Assert.Throws<InstallerProtocolException>(() => WindowsMaintenanceOriginalBaseline.Parse(bytes)); }
        finally { CryptographicOperations.ZeroMemory(original); CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public async Task ExactContentsAndUncommittedTransactionAreVerifiedUnderRetainedAuthority()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline service = Baseline(plan);
        var reader = new Reader(Contents(plan));
        var transactions = new Transactions(InstallerTransactionSnapshot.Create(service.Intent.TransitionTo(InstallerTransactionPhase.MachineReserved)));
        var authority = new Authority();

        await new WindowsMaintenanceOriginalStateGuard(service, reader.Contents, reader, transactions, authority)
            .VerifyOriginalStateAsync(plan, service, CancellationToken.None);

        Assert.Equal(2, transactions.Reads);
        Assert.Equal(2, authority.Checks);
        Assert.Equal(1, reader.Reads);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("footprint")]
    [InlineData("package-contents")]
    [InlineData("payload")]
    [InlineData("association")]
    [InlineData("trust")]
    public async Task EveryChangedOriginalEvidenceCategoryRejectsRestoration(string category)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline service = Baseline(plan);
        WindowsMaintenanceOriginalContents original = Contents(plan);
        InstallerInstalledPackage package = original.Package;
        WindowsPackageFootprint footprint = original.Footprint;
        string packageContents = original.PackageContentsSha256;
        WindowsMaintenanceFileFingerprint[] files = original.Files.ToArray();
        string association = original.AssociationSha256;
        WindowsMaintenanceTrustFingerprint trust = original.Trust;
        if (category == "package")
        {
            string version = "1.2.3.3";
            package = package with { Version = version, PackageFullName = $"{package.Name}_{version}_x64__{package.PublisherId}" };
        }
        else if (category == "footprint") { footprint = footprint with { SignatureSha256 = new string('b', 64) }; }
        else if (category == "package-contents") { packageContents = new string('b', 64); }
        else if (category == "payload") { files[0] = files[0] with { Sha256 = new string('b', 64) }; }
        else if (category == "association") { association = new string('b', 64); }
        else { trust = trust with { UserLedgerSha256 = new string('b', 64) }; }
        var reader = new Reader(new WindowsMaintenanceOriginalContents(package, footprint, packageContents, files, association, trust));
        var guard = new WindowsMaintenanceOriginalStateGuard(service, original, reader,
            new Transactions(InstallerTransactionSnapshot.Create(service.Intent)), new Authority());

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            guard.VerifyOriginalStateAsync(plan, service, CancellationToken.None));

        Assert.Equal("installer.recovery.original_state_changed", failure.DiagnosticCode);
    }

    [Fact]
    public async Task MissingTransactionCannotReadInstallationContents()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline service = Baseline(plan);
        var reader = new Reader(Contents(plan));

        await Assert.ThrowsAsync<InstallerProtocolException>(() => new WindowsMaintenanceOriginalStateGuard(service, reader.Contents,
            reader, new Transactions(null), new Authority()).VerifyOriginalStateAsync(plan, service, CancellationToken.None));

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task PackageCommitWhileReadingContentsCannotPassTheAfterReadBoundary()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline service = Baseline(plan);
        var transactions = new Transactions(InstallerTransactionSnapshot.Create(service.Intent));
        var reader = new Reader(Contents(plan))
        {
            AfterRead = () => transactions.State = InstallerTransactionSnapshot.Create(service.Intent
                .TransitionTo(InstallerTransactionPhase.MachineReserved).TransitionTo(InstallerTransactionPhase.PackageCommitted)),
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => new WindowsMaintenanceOriginalStateGuard(service, reader.Contents,
            reader, transactions, new Authority()).VerifyOriginalStateAsync(plan, service, CancellationToken.None));

        Assert.Equal(2, transactions.Reads);
    }

    [Fact]
    public async Task ExpiredRetainedAuthorityRejectsBeforeReadingPublicOrPrivateEvidence()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServicePreparationBaseline service = Baseline(plan);
        var reader = new Reader(Contents(plan));
        var transactions = new Transactions(InstallerTransactionSnapshot.Create(service.Intent));

        await Assert.ThrowsAsync<InstallerProtocolException>(() => new WindowsMaintenanceOriginalStateGuard(service, reader.Contents,
            reader, transactions, new Authority { Fail = true }).VerifyOriginalStateAsync(plan, service, CancellationToken.None));

        Assert.Equal(0, transactions.Reads);
        Assert.Equal(0, reader.Reads);
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);
    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) => WindowsMachineDeploymentPlan.Create(
        fixture.Request(InstallerOperation.Repair, Owner), fixture.Manifest, InstallerMachineAssociation.Create(Owner, Credential),
        @"C:\Program Files", @"C:\ProgramData", @"C:\Users\owner");
    private static WindowsServicePreparationBaseline Baseline(WindowsMachineDeploymentPlan plan) => WindowsServicePreparationBaseline.Capture(
        plan, InstallerTransactionJournal.Create(plan.Request), new WindowsServiceSnapshot(plan.Service, WindowsServiceRuntimeState.Running,
            WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(Owner)));
    private static WindowsMaintenanceOriginalContents Contents(WindowsMachineDeploymentPlan plan)
    {
        var identity = plan.Manifest.PackageIdentity;
        var package = new InstallerInstalledPackage(identity.Name, identity.Publisher, identity.PublisherId,
            plan.Manifest.ExpectedPackageVersion, identity.Architecture, identity.ResourceId, identity.PackageFullName, identity.PackageFamilyName, true);
        return new(package, new WindowsPackageFootprint(4, Hash, 8, Hash), Hash,
            plan.PayloadTargets.Select(target => new WindowsMaintenanceFileFingerprint(target.Source.Path,
                target.RelativeTargetPath.Replace('\\', '/'), target.Source.Length, target.Source.Sha256)).ToArray(), Hash,
            new WindowsMaintenanceTrustFingerprint(Hash, Hash, plan.Manifest.PackageCertificateThumbprint, plan.Manifest.CertificateSha256));
    }

    private sealed class Reader(WindowsMaintenanceOriginalContents contents) : IWindowsMaintenanceOriginalContentsReader
    {
        internal WindowsMaintenanceOriginalContents Contents { get; } = contents;
        internal int Reads { get; private set; }
        internal Action? AfterRead { get; init; }
        public Task<WindowsMaintenanceOriginalContents> ReadAsync(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            AfterRead?.Invoke();
            return Task.FromResult(Contents);
        }
    }
    private sealed class Transactions(InstallerTransactionSnapshot? state) : IInstallerTransactionReader
    {
        internal InstallerTransactionSnapshot? State { get; set; } = state;
        internal int Reads { get; private set; }
        public Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(State);
        }
    }
    private sealed class Authority : IWindowsMaintenanceRecoveryAuthorityLease
    {
        internal int Checks { get; private set; }
        internal bool Fail { get; init; }
        public Task ReverifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks++;
            return Fail ? throw new InstallerProtocolException("installer.recovery.authority_expired") : Task.CompletedTask;
        }
    }
}
