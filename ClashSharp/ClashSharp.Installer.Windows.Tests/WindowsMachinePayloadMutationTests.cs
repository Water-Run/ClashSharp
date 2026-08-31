using System.ComponentModel;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachinePayloadMutationTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task StageCopiesFlushesAndVerifiesEveryExactFile()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        var native = new FakeSlotNative(plan);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        await mutation.StageAsync(plan, release, CancellationToken.None);

        Assert.Equal(1, native.ResetCalls);
        Assert.Equal(7, native.CreateCalls);
        Assert.Equal(7, native.FlushCalls);
        Assert.Equal(1, native.CompleteCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.ExactMatch,
            native.Status(plan.StagingRoot));
        Assert.All(plan.PayloadTargets, target =>
        {
            byte[] bytes = native.Files[target.Source.Path];
            Assert.Equal(target.Source.Length, bytes.LongLength);
            Assert.Equal(
                target.Source.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        });
    }

    [Fact]
    public async Task StagingFailureIsSanitizedAndNeverPromotesTheLiveSlot()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        var native = new FakeSlotNative(plan)
        {
            CreateFailureAt = 2,
        };
        var mutation = new WindowsMachinePayloadMutation(native, native);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                mutation.StageAsync(plan, release, CancellationToken.None));

        Assert.Equal("installer.machine.payload_staging_failed", exception.DiagnosticCode);
        Assert.Equal(0, native.PromoteCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.CurrentRoot));
    }

    [Fact]
    public async Task PromotionCommitsExactCurrentAndRemovesBothResidueSlots()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        var native = new FakeSlotNative(plan);
        var mutation = new WindowsMachinePayloadMutation(native, native);
        await mutation.StageAsync(plan, release, CancellationToken.None);

        mutation.PromoteAndVerify(plan, CancellationToken.None);

        Assert.Equal(1, native.PromoteCalls);
        Assert.Equal(1, native.CleanupCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.ExactMatch,
            native.Status(plan.CurrentRoot));
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.StagingRoot));
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.PreviousRoot));
        mutation.VerifyInstalled(plan, CancellationToken.None);
    }

    [Fact]
    public void ReplayedPromotionSkipsSwapWhenCurrentIsAlreadyExact()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan);
        native.SetStatus(plan.CurrentRoot, WindowsMachinePayloadTreeStatus.ExactMatch);
        native.SetStatus(plan.PreviousRoot, WindowsMachinePayloadTreeStatus.Invalid);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        mutation.PromoteAndVerify(plan, CancellationToken.None);

        Assert.Equal(0, native.PromoteCalls);
        Assert.Equal(1, native.CleanupCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.PreviousRoot));
    }

    [Fact]
    public void PromotionRequiresAnIndependentlyVerifiedStagingTree()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan);
        native.SetStatus(plan.StagingRoot, WindowsMachinePayloadTreeStatus.Invalid);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            mutation.PromoteAndVerify(plan, CancellationToken.None));

        Assert.Equal("installer.machine.payload_staging_missing", exception.DiagnosticCode);
        Assert.Equal(0, native.PromoteCalls);
    }

    [Fact]
    public void LostPromotionAcknowledgementIsAcceptedOnlyAfterExactCurrentIsObserved()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan)
        {
            PromoteFailure = new Win32Exception(1726),
        };
        native.SetStatus(plan.StagingRoot, WindowsMachinePayloadTreeStatus.ExactMatch);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        mutation.PromoteAndVerify(plan, CancellationToken.None);

        Assert.Equal(1, native.PromoteCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.ExactMatch,
            native.Status(plan.CurrentRoot));
    }

    [Fact]
    public void PromotionFailureWithoutExactCurrentEndsUncertain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan)
        {
            ApplyPromote = false,
            PromoteFailure = new Win32Exception(5),
        };
        native.SetStatus(plan.StagingRoot, WindowsMachinePayloadTreeStatus.ExactMatch);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        InstallerStateUncertainException exception =
            Assert.Throws<InstallerStateUncertainException>(() =>
                mutation.PromoteAndVerify(plan, CancellationToken.None));

        Assert.Equal("installer.machine.payload_state_uncertain", exception.DiagnosticCode);
        Assert.Equal(0, native.CleanupCalls);
    }

    [Fact]
    public void CleanupFailureWithoutAbsentResidueEndsUncertain()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan)
        {
            ApplyCleanup = false,
            CleanupFailure = new Win32Exception(5),
        };
        native.SetStatus(plan.CurrentRoot, WindowsMachinePayloadTreeStatus.ExactMatch);
        native.SetStatus(plan.PreviousRoot, WindowsMachinePayloadTreeStatus.Invalid);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        InstallerStateUncertainException exception =
            Assert.Throws<InstallerStateUncertainException>(() =>
                mutation.PromoteAndVerify(plan, CancellationToken.None));

        Assert.Equal("installer.machine.payload_state_uncertain", exception.DiagnosticCode);
    }

    [Fact]
    public void LostRemovalAcknowledgementIsAcceptedAfterAllSlotsAreAbsent()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new FakeSlotNative(plan)
        {
            RemoveFailure = new Win32Exception(1726),
        };
        native.SetStatus(plan.CurrentRoot, WindowsMachinePayloadTreeStatus.ExactMatch);
        native.SetStatus(plan.StagingRoot, WindowsMachinePayloadTreeStatus.Invalid);
        native.SetStatus(plan.PreviousRoot, WindowsMachinePayloadTreeStatus.Invalid);
        var mutation = new WindowsMachinePayloadMutation(native, native);

        mutation.RemoveAndVerify(plan, CancellationToken.None);

        Assert.Equal(1, native.RemoveCalls);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.CurrentRoot));
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.StagingRoot));
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Missing,
            native.Status(plan.PreviousRoot));
    }

    [Fact]
    public async Task PreCancellationMakesNoMutationOrInspectionCalls()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (Func<WindowsMachinePayloadMutation, Task> action in new Func<WindowsMachinePayloadMutation, Task>[]
        {
            mutation => mutation.StageAsync(plan, release, cancellation.Token),
            mutation => Task.Run(() => mutation.PromoteAndVerify(plan, cancellation.Token)),
            mutation => Task.Run(() => mutation.RemoveAndVerify(plan, cancellation.Token)),
        })
        {
            var native = new FakeSlotNative(plan);
            var mutation = new WindowsMachinePayloadMutation(native, native);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action(mutation));
            Assert.Equal(0, native.MutationCalls);
            Assert.Equal(0, native.InspectCalls);
        }
    }

    [Fact]
    public async Task WindowsSlotNativeRoundTripsOnlyTheIsolatedFixtureRoot()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        Directory.CreateDirectory(plan.MachineRoot);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        var inspector = new WindowsMachinePayloadTreeVerifier();
        var mutation = new WindowsMachinePayloadMutation(
            WindowsMachinePayloadSlotNative.Instance,
            inspector);

        await mutation.StageAsync(plan, release, CancellationToken.None);
        Assert.Equal(
            WindowsMachinePayloadTreeStatus.ExactMatch,
            inspector.Inspect(plan, plan.StagingRoot, CancellationToken.None));

        mutation.PromoteAndVerify(plan, CancellationToken.None);
        mutation.VerifyInstalled(plan, CancellationToken.None);

        mutation.RemoveAndVerify(plan, CancellationToken.None);
        Assert.True(Directory.Exists(plan.MachineRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(plan.MachineRoot));
    }

    private static WindowsPayloadFixture Fixture() =>
        new(removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(
        WindowsPayloadFixture fixture,
        InstallerRequest? request = null) =>
        WindowsMachineDeploymentPlan.Create(
            request ?? fixture.Request(targetSid: TargetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", "owner"));

    private sealed class FakeSlotNative
        : IWindowsMachinePayloadSlotNative,
          IWindowsMachinePayloadTreeInspector
    {
        private readonly WindowsMachineDeploymentPlan _plan;
        private readonly Dictionary<string, WindowsMachinePayloadTreeStatus> _statuses;

        internal FakeSlotNative(WindowsMachineDeploymentPlan plan)
        {
            _plan = plan;
            _statuses = new(StringComparer.OrdinalIgnoreCase)
            {
                [plan.CurrentRoot] = WindowsMachinePayloadTreeStatus.Missing,
                [plan.StagingRoot] = WindowsMachinePayloadTreeStatus.Missing,
                [plan.PreviousRoot] = WindowsMachinePayloadTreeStatus.Missing,
            };
        }

        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        internal int? CreateFailureAt { get; init; }

        internal bool ApplyPromote { get; init; } = true;

        internal bool ApplyCleanup { get; init; } = true;

        internal bool ApplyRemove { get; init; } = true;

        internal Exception? PromoteFailure { get; init; }

        internal Exception? CleanupFailure { get; init; }

        internal Exception? RemoveFailure { get; init; }

        internal int ResetCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int FlushCalls { get; private set; }

        internal int CompleteCalls { get; private set; }

        internal int PromoteCalls { get; private set; }

        internal int CleanupCalls { get; private set; }

        internal int RemoveCalls { get; private set; }

        internal int InspectCalls { get; private set; }

        internal int MutationCalls => ResetCalls + CreateCalls + CompleteCalls
            + PromoteCalls + CleanupCalls + RemoveCalls;

        internal WindowsMachinePayloadTreeStatus Status(string root) => _statuses[root];

        internal void SetStatus(string root, WindowsMachinePayloadTreeStatus status) =>
            _statuses[root] = status;

        public void ResetStaging(WindowsMachineDeploymentPlan plan)
        {
            Assert.Same(_plan, plan);
            ResetCalls++;
            Files.Clear();
            _statuses[plan.StagingRoot] = WindowsMachinePayloadTreeStatus.Missing;
        }

        public IWindowsMachineStagingFile CreateStagingFile(
            WindowsMachineDeploymentPlan plan,
            WindowsMachinePayloadTarget target)
        {
            Assert.Same(_plan, plan);
            CreateCalls++;
            if (CreateFailureAt == CreateCalls)
            {
                throw new Win32Exception(5);
            }

            return new FakeStagingFile(bytes =>
            {
                FlushCalls++;
                Files.Add(target.Source.Path, bytes);
            });
        }

        public void CompleteStagingTree(WindowsMachineDeploymentPlan plan)
        {
            Assert.Same(_plan, plan);
            CompleteCalls++;
            bool exact = Files.Count == plan.PayloadTargets.Count
                && plan.PayloadTargets.All(target =>
                    Files.TryGetValue(target.Source.Path, out byte[]? bytes)
                    && bytes.LongLength == target.Source.Length
                    && string.Equals(
                        Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        target.Source.Sha256,
                        StringComparison.Ordinal));
            _statuses[plan.StagingRoot] = exact
                ? WindowsMachinePayloadTreeStatus.ExactMatch
                : WindowsMachinePayloadTreeStatus.Invalid;
        }

        public void PromoteStaging(WindowsMachineDeploymentPlan plan)
        {
            Assert.Same(_plan, plan);
            PromoteCalls++;
            if (ApplyPromote)
            {
                _statuses[plan.PreviousRoot] = _statuses[plan.CurrentRoot];
                _statuses[plan.CurrentRoot] = _statuses[plan.StagingRoot];
                _statuses[plan.StagingRoot] = WindowsMachinePayloadTreeStatus.Missing;
            }

            if (PromoteFailure is not null)
            {
                throw PromoteFailure;
            }
        }

        public void CleanupAfterPromotion(WindowsMachineDeploymentPlan plan)
        {
            Assert.Same(_plan, plan);
            CleanupCalls++;
            if (ApplyCleanup)
            {
                _statuses[plan.StagingRoot] = WindowsMachinePayloadTreeStatus.Missing;
                _statuses[plan.PreviousRoot] = WindowsMachinePayloadTreeStatus.Missing;
            }

            if (CleanupFailure is not null)
            {
                throw CleanupFailure;
            }
        }

        public void RemoveAllSlots(WindowsMachineDeploymentPlan plan)
        {
            Assert.Same(_plan, plan);
            RemoveCalls++;
            if (ApplyRemove)
            {
                _statuses[plan.CurrentRoot] = WindowsMachinePayloadTreeStatus.Missing;
                _statuses[plan.StagingRoot] = WindowsMachinePayloadTreeStatus.Missing;
                _statuses[plan.PreviousRoot] = WindowsMachinePayloadTreeStatus.Missing;
            }

            if (RemoveFailure is not null)
            {
                throw RemoveFailure;
            }
        }

        public WindowsMachinePayloadTreeStatus Inspect(
            WindowsMachineDeploymentPlan plan,
            string root,
            CancellationToken cancellationToken)
        {
            Assert.Same(_plan, plan);
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            return _statuses[root];
        }
    }

    private sealed class FakeStagingFile : IWindowsMachineStagingFile
    {
        private readonly MemoryStream _content = new();
        private readonly Action<byte[]> _flush;
        private bool _flushed;

        internal FakeStagingFile(Action<byte[]> flush)
        {
            _flush = flush;
        }

        public Stream Content => _content;

        public void FlushToDisk()
        {
            if (_flushed)
            {
                throw new InvalidOperationException("The fake file was flushed twice.");
            }

            _flush(_content.ToArray());
            _flushed = true;
        }

        public ValueTask DisposeAsync()
        {
            _content.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
