using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

/// <summary>
/// The presentation runtime adapter: every capability is gated by a stable refusal, disposal and
/// cancellation are honoured before the backend is touched, blocking backends never block the caller,
/// and nothing a caller supplies can reach the backend unvalidated.
/// </summary>
public sealed class ProductionInstallerRuntimeContractTests
{
    private static readonly string[] Capabilities =
    [
        "execute", "launch", "install-preferences", "transfer-preferences", "complete-preferences",
        "restore-original", "retired-uninstall", "transfer",
    ];

    public static TheoryData<string> CapabilityNames() => new(Capabilities);

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public async Task EveryEntryPointRefusesAfterDisposalBeforeTouchingTheBackend(string capability)
    {
        FullBackend backend = new();
        ProductionInstallerRuntime runtime = new(backend);
        runtime.Dispose();
        runtime.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(runtime, capability, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.InspectReadinessAsync(CancellationToken.None));

        Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public async Task AnUnsupportedCapabilityIsRefusedWithAStableCodeBothWhenAbsentAndWhenDeclinedByTheBackend(string capability)
    {
        if (capability == "execute")
        {
            return; // The base operation is always supported; its argument contract is covered separately.
        }

        foreach (IInstallerRuntimeBackend backend in new IInstallerRuntimeBackend[] { new PlainBackend(), new FullBackend { Supported = false } })
        {
            using ProductionInstallerRuntime runtime = new(backend);

            InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                Invoke(runtime, capability, CancellationToken.None));

            InstallerProtocolValidation.ValidateDiagnosticCode(refusal.DiagnosticCode);
            Assert.Equal(ExpectedUnavailableCode(capability), refusal.DiagnosticCode);
            Assert.Equal(0, (backend as FullBackend)?.Calls ?? 0);
        }

        using ProductionInstallerRuntime unsupported = new(new PlainBackend());
        Assert.False(unsupported.SupportsOwnerTransfer);
        Assert.False(unsupported.SupportsRetiredUninstall);
        Assert.False(unsupported.SupportsOriginalRestoration);
        Assert.False(unsupported.SupportsApplicationLaunch);
        Assert.False(unsupported.SupportsInstallPreferences);
        using ProductionInstallerRuntime declined = new(new FullBackend { Supported = false });
        Assert.False(declined.SupportsOwnerTransfer || declined.SupportsRetiredUninstall || declined.SupportsOriginalRestoration
            || declined.SupportsApplicationLaunch || declined.SupportsInstallPreferences);
        using ProductionInstallerRuntime supported = new(new FullBackend());
        Assert.True(supported.SupportsOwnerTransfer && supported.SupportsRetiredUninstall && supported.SupportsOriginalRestoration
            && supported.SupportsApplicationLaunch && supported.SupportsInstallPreferences);
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public async Task ACancelledTokenStopsEveryEntryPointBeforeTheBackendRuns(string capability)
    {
        FullBackend backend = new();
        using ProductionInstallerRuntime runtime = new(backend);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(runtime, capability, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.InspectReadinessAsync(cancellation.Token));

        Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public async Task SupportedEntryPointsReachTheBackendOnceAndNeverOnTheCallersThread(string capability)
    {
        FullBackend backend = new();
        using ProductionInstallerRuntime runtime = new(backend);
        int caller = Environment.CurrentManagedThreadId;
        using ManualResetEventSlim hold = new();
        backend.BlockSynchronously = hold;

        Task call = Invoke(runtime, capability, CancellationToken.None);
        // The backend blocks before its first await; the caller must still have regained control.
        Assert.False(call.IsCompleted);
        Assert.True(await Task.Run(() => backend.Entered.Wait(TimeSpan.FromSeconds(10))));
        hold.Set();
        await call.WaitAsync(TestTimeouts.Completion);

        Assert.Equal(1, backend.Calls);
        Assert.NotEqual(caller, backend.LastThread);
    }

    [Fact]
    public async Task ArgumentsAreValidatedByTheAdapterNotLeftToTheBackend()
    {
        FullBackend backend = new();
        using ProductionInstallerRuntime runtime = new(backend);
        IProgress<InstallerProgress> progress = new Progress<InstallerProgress>();
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm = static (_, _) => Task.FromResult(true);

        Assert.Throws<ArgumentNullException>(() => new ProductionInstallerRuntime(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.ExecuteAsync(InstallerOperation.Install, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.InstallWithPreferencesAsync(null!, progress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.InstallWithPreferencesAsync(InstallerPreferences.Default, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.TransferWithPreferencesAsync(null!, confirm, progress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.TransferWithPreferencesAsync(InstallerPreferences.Default, null!, progress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.TransferWithPreferencesAsync(InstallerPreferences.Default, confirm, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.CompletePreferencesAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.RestoreOriginalAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.UninstallRetiredAccountAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.TransferAndExecuteAsync(null!, progress, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.TransferAndExecuteAsync(confirm, null!, CancellationToken.None));

        foreach (InstallerOperation invalid in new[] { (InstallerOperation)(-1), (InstallerOperation)3, (InstallerOperation)99 })
        {
            InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                runtime.ExecuteAsync(invalid, progress, CancellationToken.None));
            Assert.Equal("installer.request.operation_invalid", refusal.DiagnosticCode);
        }

        // Preferences are validated here, before any backend sees them.
        InstallerPreferences bad = InstallerPreferences.Default with { Notifications = (InstallerNotificationPreset)42 };
        Assert.Equal(
            "installer.preferences.notifications_invalid",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() => runtime.InstallWithPreferencesAsync(bad, progress, CancellationToken.None))).DiagnosticCode);
        Assert.Equal(
            "installer.preferences.notifications_invalid",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() => runtime.TransferWithPreferencesAsync(bad, confirm, progress, CancellationToken.None))).DiagnosticCode);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task ResultsAndConfirmationsFlowThroughWithoutLeakingBackendOffers()
    {
        FullBackend backend = new();
        using ProductionInstallerRuntime runtime = new(backend);
        IProgress<InstallerProgress> progress = new Progress<InstallerProgress>();
        InstallerOwnerTransferOffer offer = new(
            new InstallerOwnerTransferRequest(InstallerTestHash, InstallerOperation.Install, "1.2.3.4", InstallerTestHash),
            InstallerTestHash,
            "S-1-5-21-100-200-300-1002",
            "S-1-5-21-100-200-300-1001",
            IsRecovery: true);
        backend.Offer = offer;
        List<InstallerOwnerTransferConfirmation> shown = [];
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm = (confirmation, _) =>
        {
            shown.Add(confirmation);
            return Task.FromResult(true);
        };

        InstallerExecutionResult executed = await runtime.ExecuteAsync(InstallerOperation.Repair, progress, CancellationToken.None);
        InstallerExecutionResult transferred = await runtime.TransferAndExecuteAsync(confirm, progress, CancellationToken.None);
        InstallerExecutionResult transferredWith = await runtime.TransferWithPreferencesAsync(
            InstallerPreferences.Default, confirm, progress, CancellationToken.None);

        Assert.Equal(backend.Result, executed);
        Assert.Equal(backend.Result, transferred);
        Assert.Equal(backend.Result, transferredWith);
        // The user only ever sees whether this is a recovery; accounts and credentials stay behind the port.
        Assert.Equal([new InstallerOwnerTransferConfirmation(true), new InstallerOwnerTransferConfirmation(true)], shown);
        Assert.Equal([InstallerOperation.Repair], backend.Operations);

        // A malformed offer from the backend is refused before the user is asked anything.
        backend.Offer = offer with { PreviousOwnerSid = offer.NextOwnerSid };
        shown.Clear();
        await Assert.ThrowsAsync<InstallerProtocolException>(() => runtime.TransferAndExecuteAsync(confirm, progress, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => runtime.TransferWithPreferencesAsync(InstallerPreferences.Default, confirm, progress, CancellationToken.None));
        Assert.Empty(shown);
    }

    [Fact]
    public async Task InspectionRefusesAMissingOrInvalidBackendSnapshotWithStableCodes()
    {
        PlainBackend backend = new();
        using ProductionInstallerRuntime runtime = new(backend);

        backend.Inspection = null;
        Assert.Equal(
            "installer.runtime.inspection_result_missing",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() => runtime.InspectReadinessAsync(CancellationToken.None))).DiagnosticCode);

        foreach (InstallerRuntimeInspection invalid in new[]
        {
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(true, null, false, null), null, "1.2"),
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(true, "x", false, null), null, "1.2.3.4"),
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(true, null, false, "installer.x"), null, "1.2.3.4"),
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(false, null, false, null), null, "1.2.3.4"),
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(false, null, false, "Not A Code"), null, "1.2.3.4"),
            new InstallerRuntimeInspection(new InstallerEnvironmentSnapshot(true, null, true, null), null, "1.2.3.4"),
        })
        {
            backend.Inspection = invalid;
            InstallerProtocolException refusal = await Assert.ThrowsAnyAsync<InstallerProtocolException>(() => runtime.InspectReadinessAsync(CancellationToken.None));
            InstallerProtocolValidation.ValidateDiagnosticCode(refusal.DiagnosticCode);
        }
    }

    private const string InstallerTestHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static string ExpectedUnavailableCode(string capability) => capability switch
    {
        "launch" => "installer.launch.unavailable",
        "install-preferences" or "complete-preferences" => "installer.preferences.unavailable",
        "transfer-preferences" or "transfer" => "installer.owner_transfer.unavailable",
        "restore-original" => "installer.recovery.capability_unavailable",
        "retired-uninstall" => "installer.retired_uninstall.unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    private static Task Invoke(ProductionInstallerRuntime runtime, string capability, CancellationToken token)
    {
        IProgress<InstallerProgress> progress = new Progress<InstallerProgress>();
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm = static (_, _) => Task.FromResult(true);
        return capability switch
        {
            "execute" => runtime.ExecuteAsync(InstallerOperation.Install, progress, token),
            "launch" => runtime.LaunchApplicationAsync(token),
            "install-preferences" => runtime.InstallWithPreferencesAsync(InstallerPreferences.Default, progress, token),
            "transfer-preferences" => runtime.TransferWithPreferencesAsync(InstallerPreferences.Default, confirm, progress, token),
            "complete-preferences" => runtime.CompletePreferencesAsync(progress, token),
            "restore-original" => runtime.RestoreOriginalAsync(progress, token),
            "retired-uninstall" => runtime.UninstallRetiredAccountAsync(progress, token),
            "transfer" => runtime.TransferAndExecuteAsync(confirm, progress, token),
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
    }

    private class PlainBackend : IInstallerRuntimeBackend
    {
        internal InstallerRuntimeInspection? Inspection { get; set; }

        public virtual Task<InstallerRuntimeInspection> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Inspection!);

        public virtual Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new InstallerExecutionResult(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false));

        public virtual void Dispose()
        {
        }
    }

    private sealed class FullBackend :
        PlainBackend,
        IInstallerOwnerTransferRuntimeBackend,
        IInstallerRetiredUninstallRuntimeBackend,
        IInstallerOriginalRestorationRuntimeBackend,
        IInstallerPreferencesRuntimeBackend,
        IInstallerApplicationLaunchBackend
    {
        internal bool Supported { get; init; } = true;

        internal int Calls { get; private set; }

        internal int DisposeCount { get; private set; }

        internal int LastThread { get; private set; }

        internal ManualResetEventSlim Entered { get; } = new();

        internal ManualResetEventSlim? BlockSynchronously { get; set; }

        internal List<InstallerOperation> Operations { get; } = [];

        internal InstallerOwnerTransferOffer? Offer { get; set; }

        internal InstallerExecutionResult Result { get; } =
            new(InstallerExecutionOutcome.Succeeded, "installer.test.backend", InstallerTransactionPhase.Verified, false);

        public bool SupportsOwnerTransfer => Supported;

        public bool SupportsRetiredUninstall => Supported;

        public bool SupportsOriginalRestoration => Supported;

        public bool SupportsInstallPreferences => Supported;

        public bool SupportsApplicationLaunch => Supported;

        public override void Dispose()
        {
            DisposeCount++;
            base.Dispose();
        }

        public override Task<InstallerRuntimeInspection> InspectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return base.InspectAsync(cancellationToken);
        }

        public override Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            return Enter();
        }

        public Task<InstallerExecutionResult> TransferAndExecuteAsync(
            Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => ConfirmThenEnter(confirm, cancellationToken);

        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Enter();

        public Task<InstallerExecutionResult> RestoreOriginalAsync(
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Enter();

        public Task<InstallerExecutionResult> InstallWithPreferencesAsync(
            InstallerPreferences preferences,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Enter();

        public Task<InstallerExecutionResult> TransferWithPreferencesAsync(
            InstallerPreferences preferences,
            Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => ConfirmThenEnter(confirm, cancellationToken);

        public Task<InstallerExecutionResult> CompletePreferencesAsync(
            IProgress<InstallerProgress>? progress,
            CancellationToken cancellationToken) => Enter();

        public Task LaunchApplicationAsync(CancellationToken cancellationToken) => Enter();

        private Task<InstallerExecutionResult> Enter()
        {
            Calls++;
            LastThread = Environment.CurrentManagedThreadId;
            Entered.Set();
            BlockSynchronously?.Wait(TimeSpan.FromSeconds(10));
            return Task.FromResult(Result);
        }

        private async Task<InstallerExecutionResult> ConfirmThenEnter(
            Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
            CancellationToken cancellationToken)
        {
            if (Offer is not null)
            {
                _ = await confirm(Offer, cancellationToken);
            }

            return Enter().Result;
        }
    }
}
