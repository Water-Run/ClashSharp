using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Tests;

public sealed class VerifiedInstallerPackageMutationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3.3")]
    public async Task InstallDeploysWhenMissingOrOlderAndVerifiesExactTarget(string? installedVersion)
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(installedVersion is null ? null : Installed(installedVersion));
        store.Inspections.Enqueue(Installed());
        int reverifications = 0;
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(
            reverify: (_, _) =>
            {
                reverifications++;
                return Task.CompletedTask;
            });
        var mutation = new VerifiedInstallerPackageMutation(store);

        await mutation.ApplyAsync(
            InstallerTestData.Request(),
            lease,
            CancellationToken.None);

        Assert.Equal(1, store.DeployCalls);
        Assert.Equal(0, store.RemoveCalls);
        Assert.Equal(2, store.InspectCalls);
        Assert.Equal(1, reverifications);
    }

    [Fact]
    public async Task ReplayedInstallAcceptsAnAlreadyCommittedExactTarget()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(Installed());
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await mutation.ApplyAsync(
            InstallerTestData.Request(),
            lease,
            CancellationToken.None);

        Assert.Equal(0, store.DeployCalls);
        Assert.Equal(1, store.InspectCalls);
    }

    [Fact]
    public async Task InstallRedeploysAnUnhealthyExactTarget()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(Installed(isHealthy: false));
        store.Inspections.Enqueue(Installed());
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await mutation.ApplyAsync(
            InstallerTestData.Request(),
            lease,
            CancellationToken.None);

        Assert.Equal(1, store.DeployCalls);
        Assert.Equal(2, store.InspectCalls);
    }

    [Fact]
    public async Task RepairRedeploysEvenWhenTheTargetVersionIsAlreadyRegistered()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(Installed());
        store.Inspections.Enqueue(Installed());
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await mutation.ApplyAsync(
            InstallerTestData.Request(InstallerOperation.Repair),
            lease,
            CancellationToken.None);

        Assert.Equal(1, store.DeployCalls);
        Assert.Equal(2, store.InspectCalls);
    }

    [Fact]
    public async Task RepairRequiresAnExistingRegistration()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(InstallerOperation.Repair),
                lease,
                CancellationToken.None),
            "installer.package.repair_requires_installation");

        Assert.Equal(0, store.DeployCalls);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    public async Task InstallAndRepairRejectDowngradeBeforeMutation(InstallerOperation operation)
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(Installed("1.2.3.5"));
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(operation),
                lease,
                CancellationToken.None),
            "installer.package.downgrade_rejected");

        Assert.Equal(0, store.DeployCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3.3")]
    public async Task DeployRequiresExactPostcondition(string? committedVersion)
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        store.Inspections.Enqueue(committedVersion is null ? null : Installed(committedVersion));
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.package.deployment_verification_failed");

        Assert.Equal(1, store.DeployCalls);
    }

    [Fact]
    public async Task DeployRejectsAnUnhealthyTargetPostcondition()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        store.Inspections.Enqueue(Installed(isHealthy: false));
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.package.deployment_verification_failed");

        Assert.Equal(1, store.DeployCalls);
    }

    [Fact]
    public async Task InstallRequiresAvailableLockedPayload()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        VerifiedInstallerRelease release = InstallerTestData.Release(
            packagePayloadAvailable: false);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(release);
        var mutation = new VerifiedInstallerPackageMutation(store);

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.release.package_payload_missing");

        Assert.Equal(0, store.DeployCalls);
    }

    [Fact]
    public async Task UninstallIsIdempotentWhenRegistrationIsAlreadyAbsent()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await mutation.ApplyAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall),
            lease,
            CancellationToken.None);

        Assert.Equal(0, store.RemoveCalls);
        Assert.Equal(1, store.InspectCalls);
    }

    [Fact]
    public async Task UninstallRemovesTheExactObservedRegistrationAndVerifiesAbsence()
    {
        InstallerInstalledPackage installed = Installed("1.2.3.5");
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(installed);
        store.Inspections.Enqueue(null);
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await mutation.ApplyAsync(
            InstallerTestData.Request(InstallerOperation.Uninstall),
            lease,
            CancellationToken.None);

        Assert.Equal(1, store.RemoveCalls);
        Assert.Same(installed, store.RemovedPackage);
        Assert.Equal(2, store.InspectCalls);
    }

    [Fact]
    public async Task UninstallRejectsARegistrationThatRemainsAfterRemoval()
    {
        InstallerInstalledPackage installed = Installed();
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(installed);
        store.Inspections.Enqueue(installed);
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(InstallerOperation.Uninstall),
                lease,
                CancellationToken.None),
            "installer.package.removal_verification_failed");

        Assert.Equal(1, store.RemoveCalls);
    }

    [Fact]
    public async Task ForeignButCanonicalRegistrationIsRejectedBeforeMutation()
    {
        InstallerDependencyPackageIdentity dependency = InstallerTestData.DependencyIdentity();
        var foreign = new InstallerInstalledPackage(
            dependency.Name,
            dependency.Publisher,
            dependency.PublisherId,
            dependency.Version,
            dependency.Architecture,
            dependency.ResourceId,
            dependency.PackageFullName,
            dependency.PackageFamilyName,
            IsHealthy: true);
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(foreign);
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.package.installed_identity_mismatch");

        Assert.Equal(0, store.DeployCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    [Fact]
    public async Task InvalidRegistrationIdentityIsRejectedBeforeMutation()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(Installed() with { PackageFullName = "invalid" });
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.package.installed_identity_invalid");

        Assert.Equal(0, store.DeployCalls);
    }

    [Fact]
    public async Task ReverificationFailurePreventsDeployment()
    {
        var store = new ScriptedPackageStore();
        store.Inspections.Enqueue(null);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(
            reverify: static (_, _) => throw new InstallerProtocolException(
                "installer.test.reverify_failed"));
        var mutation = new VerifiedInstallerPackageMutation(store);

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.test.reverify_failed");

        Assert.Equal(0, store.DeployCalls);
    }

    [Fact]
    public async Task PreCancelledRequestNeverCallsThePackageStore()
    {
        var store = new ScriptedPackageStore();
        var mutation = new VerifiedInstallerPackageMutation(store);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => mutation.ApplyAsync(
            InstallerTestData.Request(),
            lease,
            cancellation.Token));

        Assert.Equal(0, store.InspectCalls);
        Assert.Equal(0, store.DeployCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    [Fact]
    public async Task RequestMustMatchTheVerifiedReleaseIdentity()
    {
        var store = new ScriptedPackageStore();
        var mutation = new VerifiedInstallerPackageMutation(store);
        VerifiedInstallerRelease release = InstallerTestData.Release(
            installerHash: InstallerTestData.OtherHash);
        await using TestInstallerReleaseLease lease = InstallerTestData.Lease(release);

        await AssertDiagnosticAsync(
            () => mutation.ApplyAsync(
                InstallerTestData.Request(),
                lease,
                CancellationToken.None),
            "installer.release.identity_mismatch");

        Assert.Equal(0, store.InspectCalls);
    }

    private static InstallerInstalledPackage Installed(
        string version = InstallerTestData.Version,
        bool isHealthy = true) =>
        new(
            InstallerTestData.PackageName,
            InstallerTestData.PackagePublisher,
            InstallerTestData.PackagePublisherId,
            version,
            "x64",
            string.Empty,
            $"{InstallerTestData.PackageName}_{version}_x64__{InstallerTestData.PackagePublisherId}",
            $"{InstallerTestData.PackageName}_{InstallerTestData.PackagePublisherId}",
            isHealthy);

    private static async Task AssertDiagnosticAsync(
        Func<Task> action,
        string expectedCode)
    {
        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            action);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
    }

    private sealed class ScriptedPackageStore : IInstallerPackageStoreAdapter
    {
        internal Queue<InstallerInstalledPackage?> Inspections { get; } = [];

        internal int InspectCalls { get; private set; }

        internal int DeployCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal InstallerInstalledPackage? RemovedPackage { get; private set; }

        public Task<InstallerInstalledPackage?> InspectAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            return Task.FromResult(Inspections.Dequeue());
        }

        public Task DeployAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeployCalls++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerInstalledPackage installedPackage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCalls++;
            RemovedPackage = installedPackage;
            return Task.CompletedTask;
        }
    }
}
