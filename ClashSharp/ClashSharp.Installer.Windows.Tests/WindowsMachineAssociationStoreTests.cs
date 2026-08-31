using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineAssociationStoreTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string OtherToken =
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    [Fact]
    public async Task MissingInvalidJsonAndUnsafeObjectsHaveDistinctObservations()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative();
        using var store = new WindowsMachineAssociationStore(plan, root, native);

        InstallerMachineAssociationObservation missing =
            await store.InspectAsync(CancellationToken.None);
        native.Observation = new(
            WindowsMachineAssociationFileStatus.OrdinaryFile,
            [0x01]);
        InstallerMachineAssociationObservation invalidJson =
            await store.InspectAsync(CancellationToken.None);
        native.Observation = new(
            WindowsMachineAssociationFileStatus.Unsafe,
            null);
        InstallerMachineAssociationObservation unsafeObject =
            await store.InspectAsync(CancellationToken.None);

        Assert.Equal(InstallerMachineAssociationStatus.Missing, missing.Status);
        Assert.Equal(InstallerMachineAssociationStatus.Invalid, invalidJson.Status);
        Assert.Equal(InstallerMachineAssociationStatus.Invalid, unsafeObject.Status);
        Assert.Equal(3, root.EnsureCalls);
    }

    [Fact]
    public async Task ExactWriteIsVerifiedAndReplayDoesNotRewrite()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative();
        using var store = new WindowsMachineAssociationStore(plan, root, native);

        await store.WriteAndVerifyAsync(plan.Association, CancellationToken.None);
        await store.WriteAndVerifyAsync(plan.Association, CancellationToken.None);
        await store.VerifyExactAsync(CancellationToken.None);

        Assert.Equal(1, native.WriteCalls);
        Assert.Equal(plan.Association, Parse(native.Observation));
    }

    [Fact]
    public async Task OrdinaryInstallCannotOverwriteInvalidOrDifferentAssociation()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        foreach (WindowsMachineAssociationFileObservation observation in new[]
        {
            new WindowsMachineAssociationFileObservation(
                WindowsMachineAssociationFileStatus.Unsafe,
                null),
            Encoded(InstallerMachineAssociation.Create(TargetSid, OtherToken)),
            Encoded(InstallerMachineAssociation.Create(OtherSid, OtherToken)),
        })
        {
            var root = new FakeRootGuard(plan);
            var native = new FakeAssociationNative { Observation = observation };
            using var store = new WindowsMachineAssociationStore(plan, root, native);

            InstallerProtocolException exception =
                await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                    store.WriteAndVerifyAsync(plan.Association, CancellationToken.None));

            Assert.Equal("installer.machine.association_conflict", exception.DiagnosticCode);
            Assert.Equal(0, native.WriteCalls);
        }
    }

    [Fact]
    public async Task ExplicitReassociationRepairMayReplaceForeignAssociation()
    {
        using var fixture = Fixture();
        InstallerRequest repair = fixture.Request(targetSid: TargetSid) with
        {
            Operation = InstallerOperation.Repair,
            AllowReassociation = true,
        };
        WindowsMachineDeploymentPlan plan = Plan(fixture, request: repair);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative
        {
            Observation = Encoded(
                InstallerMachineAssociation.Create(OtherSid, OtherToken)),
        };
        using var store = new WindowsMachineAssociationStore(plan, root, native);

        await store.WriteAndVerifyAsync(plan.Association, CancellationToken.None);

        Assert.Equal(plan.Association, Parse(native.Observation));
        Assert.Equal(1, native.WriteCalls);
    }

    [Fact]
    public async Task LostWriteAcknowledgementIsAcceptedOnlyAfterExactBytesAreObserved()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative
        {
            WriteFailure = new Win32Exception(1726),
        };
        using var store = new WindowsMachineAssociationStore(plan, root, native);

        await store.WriteAndVerifyAsync(plan.Association, CancellationToken.None);

        Assert.Equal(plan.Association, Parse(native.Observation));
    }

    [Fact]
    public async Task FailedWriteWithoutExactBytesEndsUncertain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative
        {
            ApplyWrite = false,
            WriteFailure = new Win32Exception(5),
        };
        using var store = new WindowsMachineAssociationStore(plan, root, native);

        InstallerStateUncertainException exception =
            await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
                store.WriteAndVerifyAsync(plan.Association, CancellationToken.None));

        Assert.Equal(
            "installer.machine.association_state_uncertain",
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task DeleteRequiresExactAssociationAndReconcilesLostAcknowledgement()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var conflictNative = new FakeAssociationNative
        {
            Observation = Encoded(
                InstallerMachineAssociation.Create(TargetSid, OtherToken)),
        };
        using (var conflict = new WindowsMachineAssociationStore(
                   plan,
                   new FakeRootGuard(plan),
                   conflictNative))
        {
            InstallerProtocolException exception =
                await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                    conflict.DeleteAndVerifyAsync(CancellationToken.None));
            Assert.Equal("installer.machine.association_conflict", exception.DiagnosticCode);
            Assert.Equal(0, conflictNative.DeleteCalls);
        }

        var native = new FakeAssociationNative
        {
            Observation = Encoded(plan.Association),
            DeleteFailure = new Win32Exception(1726),
        };
        using var store = new WindowsMachineAssociationStore(
            plan,
            new FakeRootGuard(plan),
            native);

        await store.DeleteAndVerifyAsync(CancellationToken.None);
        await store.DeleteAndVerifyAsync(CancellationToken.None);
        await store.VerifyAbsentAsync(CancellationToken.None);

        Assert.Equal(1, native.DeleteCalls);
    }

    [Fact]
    public async Task PreCancellationMakesNoRootOrFileCalls()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var root = new FakeRootGuard(plan);
        var native = new FakeAssociationNative();
        using var store = new WindowsMachineAssociationStore(plan, root, native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.InspectAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAndVerifyAsync(plan.Association, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteAndVerifyAsync(cancellation.Token));

        Assert.Equal(0, root.EnsureCalls);
        Assert.Equal(0, native.ReadCalls);
        Assert.Equal(0, native.WriteCalls);
        Assert.Equal(0, native.DeleteCalls);
    }

    [Fact]
    public async Task WindowsFileNativeRoundTripsOnlyTheIsolatedFixturePath()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        Directory.CreateDirectory(plan.ServiceDataRoot);
        using var store = new WindowsMachineAssociationStore(
            plan,
            new FakeRootGuard(plan),
            WindowsMachineAssociationFileNative.Instance);

        Assert.Equal(
            InstallerMachineAssociationStatus.Missing,
            (await store.InspectAsync(CancellationToken.None)).Status);
        await store.WriteAndVerifyAsync(plan.Association, CancellationToken.None);
        Assert.Equal(plan.Association, (await store.InspectAsync(CancellationToken.None)).Association);
        Assert.True(File.Exists(plan.AssociationPath));
        Assert.False(File.Exists(plan.AssociationPath + ".new"));

        await store.DeleteAndVerifyAsync(CancellationToken.None);
        Assert.False(File.Exists(plan.AssociationPath));
        Assert.False(File.Exists(plan.AssociationPath + ".new"));
    }

    private static InstallerMachineAssociation? Parse(
        WindowsMachineAssociationFileObservation observation) =>
        observation.Bytes is null
            ? null
            : InstallerMachineAssociationCodec.Parse(observation.Bytes);

    private static WindowsMachineAssociationFileObservation Encoded(
        InstallerMachineAssociation association) =>
        new(
            WindowsMachineAssociationFileStatus.OrdinaryFile,
            InstallerMachineAssociationCodec.Serialize(association));

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(
        WindowsPayloadFixture fixture,
        string targetSid = TargetSid,
        InstallerRequest? request = null) =>
        WindowsMachineDeploymentPlan.Create(
            request ?? fixture.Request(targetSid: targetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(targetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", "owner"));

    private sealed class FakeRootGuard : IWindowsMachineRootGuard
    {
        private readonly WindowsMachineDeploymentPlan _plan;

        internal FakeRootGuard(WindowsMachineDeploymentPlan plan)
        {
            _plan = plan;
        }

        internal int EnsureCalls { get; private set; }

        public Task EnsureProtectedAsync(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            Assert.Same(_plan, plan);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAssociationNative : IWindowsMachineAssociationFileNative
    {
        private WindowsMachineAssociationFileObservation _observation = new(
            WindowsMachineAssociationFileStatus.Missing,
            null);

        internal WindowsMachineAssociationFileObservation Observation
        {
            get => Clone(_observation);
            set => _observation = Clone(value);
        }

        internal bool ApplyWrite { get; init; } = true;

        internal bool ApplyDelete { get; init; } = true;

        internal Exception? WriteFailure { get; init; }

        internal Exception? DeleteFailure { get; init; }

        internal int ReadCalls { get; private set; }

        internal int WriteCalls { get; private set; }

        internal int DeleteCalls { get; private set; }

        public WindowsMachineAssociationFileObservation Read(string path)
        {
            Assert.EndsWith("association.json", path, StringComparison.Ordinal);
            ReadCalls++;
            return Clone(_observation);
        }

        public void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
        {
            Assert.EndsWith("association.json", path, StringComparison.Ordinal);
            WriteCalls++;
            if (ApplyWrite)
            {
                _observation = new(
                    WindowsMachineAssociationFileStatus.OrdinaryFile,
                    bytes.ToArray());
            }

            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public void Delete(string path)
        {
            Assert.EndsWith("association.json", path, StringComparison.Ordinal);
            DeleteCalls++;
            if (ApplyDelete)
            {
                _observation = new(
                    WindowsMachineAssociationFileStatus.Missing,
                    null);
            }

            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }
        }

        private static WindowsMachineAssociationFileObservation Clone(
            WindowsMachineAssociationFileObservation observation) =>
            observation with { Bytes = observation.Bytes?.ToArray() };
    }
}
