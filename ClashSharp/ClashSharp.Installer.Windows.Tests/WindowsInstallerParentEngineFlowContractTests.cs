using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// State-machine contracts of the parent engine that sit above the single-operation tests: the
/// single-flight gate across every entry point, capability reporting, disposal, cancellation at every
/// await and the "core verified, user setup retryable" split.
/// </summary>
public sealed class WindowsInstallerParentEngineFlowContractTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Concurrent = "installer.concurrent_action_rejected";

    public static TheoryData<string> OperationNames =>
    [
        "execute", "restore", "retired", "transfer", "install-prefs", "transfer-prefs", "complete-prefs",
    ];

    public static TheoryData<string> OptionalOperationNames =>
    [
        "restore", "retired", "transfer", "install-prefs", "transfer-prefs", "complete-prefs",
    ];

    public static TheoryData<string, string> HolderAndProbe
    {
        get
        {
            var data = new TheoryData<string, string>();
            string[] probes =
            [
                "execute", "restore", "retired", "transfer", "install-prefs", "transfer-prefs",
                "complete-prefs", "inspect", "launch",
            ];
            foreach (string holder in new[]
            {
                "execute", "restore", "retired", "transfer", "install-prefs", "transfer-prefs", "complete-prefs",
            })
            {
                foreach (string probe in probes)
                {
                    data.Add(holder, probe);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> RecoverableUserSetupFailures =>
    [
        nameof(IOException), nameof(UnauthorizedAccessException), "Win32Exception5", "Win32Exception1223",
        nameof(InvalidOperationException), nameof(TimeoutException), "ComException", "SharingViolation",
        "DiskFull", "PathTooLong", nameof(NotSupportedException),
    ];

    [Fact]
    public void CapabilitiesAreReportedFromTheComposedFactoryAndNothingElse()
    {
        using var plain = new Harness(plainFactory: true);
        Assert.False(plain.Engine.SupportsOwnerTransfer);
        Assert.False(plain.Engine.SupportsRetiredUninstall);
        Assert.False(plain.Engine.SupportsOriginalRestoration);
        Assert.False(plain.Engine.SupportsInstallPreferences);
        Assert.True(plain.Engine.SupportsApplicationLaunch);

        using var full = new Harness();
        Assert.True(full.Engine.SupportsOwnerTransfer);
        Assert.True(full.Engine.SupportsRetiredUninstall);
        Assert.True(full.Engine.SupportsOriginalRestoration);
        Assert.False(full.Engine.SupportsInstallPreferences);

        using var withPreferences = new Harness(preferences: true);
        Assert.True(withPreferences.Engine.SupportsInstallPreferences);
    }

    [Fact]
    public async Task InspectionPublishesPendingSetupOnlyWhenNoDurableTransactionRemainsAndFollowsTheStoredServiceProfile()
    {
        using var harness = new Harness(preferences: true);
        harness.Store.Pending = harness.Document(coreVerified: true);

        InstallerRuntimeInspection clean = await harness.Engine.InspectAsync(CancellationToken.None);
        Assert.True(clean.PendingUserPreferences);

        harness.Inspector.Result = harness.Inspection(durable: true);
        InstallerRuntimeInspection withDurable = await harness.Engine.InspectAsync(CancellationToken.None);
        Assert.False(withDurable.PendingUserPreferences);

        harness.Store.Pending = harness.Document(coreVerified: false);
        harness.Inspector.Result = harness.Inspection();
        Assert.False((await harness.Engine.InspectAsync(CancellationToken.None)).PendingUserPreferences);

        Assert.True(harness.Engine.SupportsOwnerTransfer);
        harness.Store.Pending = harness.Document(coreVerified: true, preferences: InstallerPreferences.Default with
        {
            InstallTransparentProxyService = false,
        });
        _ = await harness.Engine.InspectAsync(CancellationToken.None);
        Assert.False(harness.Engine.SupportsOwnerTransfer);
        Assert.Equal(0, harness.Factory.Creates);
    }

    [Theory]
    [MemberData(nameof(OptionalOperationNames))]
    public async Task UnavailableCapabilityFailsWithItsStableCodeWithoutTouchingAnyResource(string operation)
    {
        using var harness = new Harness(plainFactory: true);
        string expected = operation switch
        {
            "restore" => "installer.recovery.capability_unavailable",
            "retired" => "installer.retired_uninstall.unavailable",
            "transfer" => "installer.owner_transfer.unavailable",
            _ => "installer.preferences.unavailable",
        };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => harness.Start(operation, CancellationToken.None));

        Assert.Equal(expected, failure.DiagnosticCode);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Equal(0, harness.Plain!.Creates);
        InstallerExecutionResult result = await harness.Engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(HolderAndProbe))]
    public async Task EveryEntryPointIsRejectedWhileAnyOtherOperationHoldsTheSingleFlightGate(string holder, string probe)
    {
        using var harness = new Harness(preferences: true, gated: true);
        harness.Store.Pending = harness.Document(coreVerified: true);
        Task<string> first = harness.Observe(holder, CancellationToken.None);
        try
        {
            await harness.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            int creates = harness.Factory.Creates;
            int stages = harness.Store.Stages;
            int acquisitions = harness.Lock.Acquisitions;

            // A second caller that were admitted would park on the same gate; bound the wait so a
            // missing rejection fails instead of hanging the run.
            string second = await harness.Observe(probe, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            string expected = (probe is "inspect" or "launch" ? "throws:" : "Blocked:") + Concurrent;
            Assert.Equal(expected, second);
            Assert.Equal(creates, harness.Factory.Creates);
            Assert.Equal(stages, harness.Store.Stages);
            Assert.Equal(acquisitions, harness.Lock.Acquisitions);
        }
        finally
        {
            harness.Factory.Release.TrySetResult();
        }

        _ = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(harness.Lock.Held);
        harness.Factory.Gated = false;
        harness.Store.Pending = null;
        InstallerExecutionResult after = await harness.Engine.ExecuteAsync(
            InstallerOperation.Install,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, after.Outcome);
        Assert.Equal(harness.Lock.Acquisitions, harness.Lock.Releases);
    }

    [Theory]
    [MemberData(nameof(OperationNames))]
    public async Task DisposedEngineRejectsEveryEntryPointBeforeAnyResourceIsTouched(string operation)
    {
        using var harness = new Harness(preferences: true);
        harness.Engine.Dispose();
        harness.Engine.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Start(operation, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Engine.InspectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Engine.LaunchApplicationAsync(CancellationToken.None));

        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Equal(0, harness.Factory.Creates);
        Assert.Equal(0, harness.Inspector.Calls);
        Assert.Equal(0, harness.Store.Stages);
    }

    [Theory]
    [InlineData(EngineStage.Inspect)]
    [InlineData(EngineStage.CreateSession)]
    [InlineData(EngineStage.SessionExecute)]
    public async Task CancellationAtEachExecutionAwaitReleasesEveryResourceAndLeavesTheEngineReusable(EngineStage stage)
    {
        using var harness = new Harness(preferences: true);
        using var cancellation = new CancellationTokenSource();
        harness.Factory.OnStage = reached =>
        {
            if (reached == stage)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            cancellation.Token));

        Assert.False(harness.Lock.Held);
        Assert.Equal(harness.Lock.Acquisitions, harness.Lock.Releases);
        Assert.Equal(harness.Factory.Creates, harness.Factory.Disposals);
        Assert.Equal(0, harness.Store.Completions);
        if (stage == EngineStage.Inspect)
        {
            Assert.Equal(0, harness.Store.Stages);
            Assert.Null(harness.Store.Pending);
        }
        else
        {
            Assert.False(harness.Store.Pending!.CoreVerified);
        }

        harness.Factory.OnStage = null;
        InstallerExecutionResult retry = await harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Null(harness.Store.Pending);
        Assert.NotNull(harness.Store.Installed);
    }

    [Fact]
    public async Task CancellationDuringUserSetupNeverDiscardsTheVerifiedCoreResultOrItsRetryableIntent()
    {
        using var harness = new Harness(preferences: true);
        using var cancellation = new CancellationTokenSource();
        harness.Setup.OnApply = cancellation.Cancel;

        InstallerExecutionResult result = await harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            cancellation.Token);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending));
        Assert.Equal("installer.preferences.pending", result.DiagnosticCode);
        Assert.True(harness.Store.Pending!.CoreVerified);
        Assert.Null(harness.Store.Installed);
        Assert.Equal(1, harness.Factory.Creates);
        Assert.Equal(1, harness.Factory.Disposals);
        Assert.False(harness.Lock.Held);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedServiceProfileReachesTheExactRequestAndTheStagedIntent(bool includeService)
    {
        using var harness = new Harness(preferences: true);
        InstallerPreferences selected = InstallerPreferences.Default with
        {
            InstallTransparentProxyService = includeService,
            EnableMcpServer = true,
            Notifications = InstallerNotificationPreset.CriticalOnly,
        };

        InstallerExecutionResult result = await harness.Engine.InstallWithPreferencesAsync(
            selected,
            progress: null,
            CancellationToken.None);

        InstallerRequest request = Assert.Single(harness.Factory.Requests);
        Assert.Equal(includeService, request.InstallTransparentProxyService);
        Assert.Equal(InstallerOperation.Install, request.Operation);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(selected, harness.Store.Installed!.Preferences);
        Assert.True(harness.Store.Installed.CoreVerified);
        Assert.Null(harness.Store.Pending);
        Assert.Equal(1, harness.Setup.Calls);
    }

    [Theory]
    [InlineData(InstallerExecutionOutcome.Failed)]
    [InlineData(InstallerExecutionOutcome.Blocked)]
    [InlineData(InstallerExecutionOutcome.Uncertain)]
    [InlineData(InstallerExecutionOutcome.Cancelled)]
    public async Task UserSetupRunsOnlyAfterTheCoreOutcomeIsSucceeded(InstallerExecutionOutcome outcome)
    {
        using var harness = new Harness(preferences: true);
        harness.Factory.ExecuteResult = new InstallerExecutionResult(
            outcome,
            "installer.test.core_not_verified",
            LastDurablePhase: null,
            RecoveryPending: outcome is InstallerExecutionOutcome.Failed or InstallerExecutionOutcome.Uncertain);

        InstallerExecutionResult result = await harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("installer.test.core_not_verified", result.DiagnosticCode);
        Assert.Equal(InstallerPreferencesWarnings.None, result.PreferencesWarnings);
        Assert.Equal(0, harness.Setup.Calls);
        Assert.False(harness.Store.Pending!.CoreVerified);
        Assert.Null(harness.Store.Installed);
    }

    [Fact]
    public async Task RunningApplicationBlocksBeforeAnyIntentIsStagedOrSessionCreated()
    {
        using var harness = new Harness(preferences: true, environment: new InstallerEnvironmentSnapshot(
            true,
            InstalledPackageVersion: "1.2.3.4",
            IsApplicationRunning: true,
            BlockingDiagnosticCode: null));

        InstallerExecutionResult result = await harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.application_running", result.DiagnosticCode);
        Assert.Equal(0, harness.Factory.Creates);
        Assert.Equal(0, harness.Store.Stages);
        Assert.False(harness.Lock.Held);
    }

    [Theory]
    [MemberData(nameof(RecoverableUserSetupFailures))]
    public async Task EveryRecoverableUserSetupFailureDegradesToRetryableSetupAndNeverRollsBackCore(string failure)
    {
        using var harness = new Harness(preferences: true);
        harness.Setup.Failure = CreateFailure(failure);

        InstallerExecutionResult result = await harness.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("installer.preferences.pending", result.DiagnosticCode);
        Assert.True(result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending));
        Assert.True(harness.Store.Pending!.CoreVerified);
        Assert.Equal(1, harness.Factory.Creates);
        Assert.Single(harness.Factory.Requests);
    }

    [Fact]
    public async Task ProtocolFailureFromUserSetupKeepsItsOwnStableCodeAndFatalFailurePropagates()
    {
        using var protocol = new Harness(preferences: true);
        protocol.Setup.Failure = new InstallerProtocolException("installer.preferences.bootstrap_request_conflict");
        InstallerExecutionResult result = await protocol.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None);
        Assert.Equal("installer.preferences.bootstrap_request_conflict", result.DiagnosticCode);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);

        using var fatal = new Harness(preferences: true);
        fatal.Setup.Failure = new FatalTestException("sentinel");
        await Assert.ThrowsAsync<FatalTestException>(() => fatal.Engine.InstallWithPreferencesAsync(
            InstallerPreferences.Default,
            progress: null,
            CancellationToken.None));
        Assert.False(fatal.Lock.Held);
        Assert.Equal(1, fatal.Factory.Disposals);
        Assert.True(fatal.Store.Pending!.CoreVerified);
    }

    [Fact]
    public async Task VerifiedUninstallRemovesOnlyTheCapturedUserIntentAndAShellFailureIsRetryable()
    {
        using var clean = new Harness(preferences: true);
        clean.Store.Installed = clean.Document(coreVerified: true);
        clean.Store.Pending = clean.Document(coreVerified: false);
        InstallerExecutionResult removed = await clean.Engine.ExecuteAsync(
            InstallerOperation.Uninstall,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, removed.Outcome);
        Assert.Equal(InstallerPreferencesWarnings.None, removed.PreferencesWarnings);
        Assert.Null(clean.Store.Installed);
        Assert.Null(clean.Store.Pending);
        Assert.Equal(1, clean.Shell.RemoveCalls);
        Assert.Equal(0, clean.Setup.Calls);

        using var failing = new Harness(preferences: true);
        failing.Store.Installed = failing.Document(coreVerified: true);
        failing.Shell.RemovalFailure = new IOException("shortcut in use");
        InstallerExecutionResult degraded = await failing.Engine.ExecuteAsync(
            InstallerOperation.Uninstall,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, degraded.Outcome);
        Assert.True(degraded.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending));
        Assert.NotNull(failing.Store.Installed);
        Assert.Equal(0, failing.Store.Removals);
    }

    [Theory]
    [InlineData(InstallerExecutionOutcome.Failed)]
    [InlineData(InstallerExecutionOutcome.Uncertain)]
    public async Task FailedUninstallNeverRemovesUserIntentOrShortcuts(InstallerExecutionOutcome outcome)
    {
        using var harness = new Harness(preferences: true);
        harness.Store.Installed = harness.Document(coreVerified: true);
        harness.Factory.ExecuteResult = new InstallerExecutionResult(
            outcome,
            "installer.test.failed",
            LastDurablePhase: null,
            RecoveryPending: true);

        InstallerExecutionResult result = await harness.Engine.ExecuteAsync(
            InstallerOperation.Uninstall,
            progress: null,
            CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.NotNull(harness.Store.Installed);
        Assert.Equal(0, harness.Shell.RemoveCalls);
    }

    [Fact]
    public async Task PendingSetupCompletionRequiresTheVerifiedReleaseAndNoRunningApplicationOrDurableTransaction()
    {
        foreach ((string label, Func<Harness, InstallerRuntimeInspection> make) in BlockingInspections())
        {
            using var harness = new Harness(preferences: true);
            harness.Store.Pending = harness.Document(coreVerified: true);
            harness.Inspector.Result = make(harness);

            InstallerExecutionResult result = await harness.Engine.CompletePreferencesAsync(
                progress: null,
                CancellationToken.None);

            Assert.True(
                result is
                {
                    Outcome: InstallerExecutionOutcome.Blocked,
                    DiagnosticCode: "installer.preferences.core_verification_required",
                },
                label);
            Assert.Equal(0, harness.Setup.Calls);
            Assert.True(harness.Store.Pending!.CoreVerified);
            Assert.False(harness.Lock.Held);
        }
    }

    [Fact]
    public async Task PendingSetupCompletionAppliesTheIntentOnceAndReportsRetryableFailureAsSuccess()
    {
        using var harness = new Harness(preferences: true);
        harness.Store.Pending = harness.Document(coreVerified: true);
        harness.Inspector.Result = harness.Inspection(installedVersion: "1.2.3.4");

        InstallerExecutionResult applied = await harness.Engine.CompletePreferencesAsync(
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, applied.Outcome);
        Assert.Equal("installer.completed", applied.DiagnosticCode);
        Assert.Equal(InstallerTransactionPhase.Verified, applied.LastDurablePhase);
        Assert.False(applied.RecoveryPending);
        Assert.Null(harness.Store.Pending);
        Assert.NotNull(harness.Store.Installed);
        Assert.Equal(0, harness.Factory.Creates);

        harness.Store.Pending = harness.Document(coreVerified: true);
        harness.Setup.Failure = new IOException("denied");
        InstallerExecutionResult retry = await harness.Engine.CompletePreferencesAsync(
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, retry.Outcome);
        Assert.Equal("installer.preferences.pending", retry.DiagnosticCode);
        Assert.True(retry.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending));
        Assert.NotNull(harness.Store.Pending);
    }

    [Fact]
    public async Task PendingSetupCompletionWithoutAnIntentFailsClosedAndReleasesTheLease()
    {
        using var harness = new Harness(preferences: true);
        harness.Inspector.Result = harness.Inspection(installedVersion: "1.2.3.4");

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => harness.Engine.CompletePreferencesAsync(progress: null, CancellationToken.None));

        Assert.Equal("installer.preferences.pending_missing", failure.DiagnosticCode);
        Assert.False(harness.Lock.Held);
        Assert.Equal(harness.Lock.Acquisitions, harness.Lock.Releases);
    }

    [Fact]
    public async Task OriginalRestorationBuildsTheExactRepairRequestAndRequiresTheRestorationCapability()
    {
        using var harness = new Harness();
        InstallerExecutionResult result = await harness.Engine.RestoreOriginalAsync(progress: null, CancellationToken.None);

        InstallerRequest request = Assert.Single(harness.Factory.Requests);
        Assert.Equal(InstallerOperation.Repair, request.Operation);
        Assert.Equal(TargetSid, request.TargetSid);
        Assert.False(request.AllowReassociation);
        Assert.Equal(harness.Fixture.Manifest.ExpectedPackageVersion, request.ExpectedPackageVersion);
        Assert.Equal(harness.Fixture.Manifest.InstallerPayloadSha256, request.InstallerPayloadSha256);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, harness.Factory.Disposals);

        using var downgraded = new Harness(restoringSessions: false);
        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => downgraded.Engine.RestoreOriginalAsync(progress: null, CancellationToken.None));
        Assert.Equal("installer.recovery.capability_unavailable", failure.DiagnosticCode);
        Assert.Equal(1, downgraded.Factory.Creates);
        Assert.Equal(1, downgraded.Factory.Disposals);
        Assert.False(downgraded.Lock.Held);
        Assert.Empty(downgraded.Factory.Requests);
    }

    [Fact]
    public async Task RetiredUninstallMapsExplicitUserCancellationAndPropagatesCallerCancellation()
    {
        using var harness = new Harness();
        harness.Factory.RetiredFailure = new InstallerUserCancelledException("installer.elevation.user_cancelled");

        InstallerExecutionResult cancelled = await harness.Engine.UninstallRetiredAccountAsync(
            progress: null,
            CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal("installer.elevation.user_cancelled", cancelled.DiagnosticCode);
        Assert.False(cancelled.RecoveryPending);
        Assert.Null(cancelled.LastDurablePhase);
        Assert.False(harness.Lock.Held);

        harness.Factory.RetiredFailure = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Engine.UninstallRetiredAccountAsync(progress: null, cancellation.Token));
        Assert.Equal(1, harness.Factory.Retired);
        InstallerExecutionResult retry = await harness.Engine.UninstallRetiredAccountAsync(
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, retry.Outcome);
    }

    [Fact]
    public async Task RetiredUninstallRemovesCapturedUserStateOnlyAfterVerifiedSuccess()
    {
        using var harness = new Harness(preferences: true);
        harness.Store.Installed = harness.Document(coreVerified: true);
        harness.Factory.RetiredResult = new InstallerExecutionResult(
            InstallerExecutionOutcome.Failed,
            "installer.test.retired_failed",
            LastDurablePhase: null,
            RecoveryPending: true);
        InstallerExecutionResult failed = await harness.Engine.UninstallRetiredAccountAsync(
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Failed, failed.Outcome);
        Assert.NotNull(harness.Store.Installed);
        Assert.Equal(0, harness.Shell.RemoveCalls);

        harness.Factory.RetiredResult = EngineSessionFactory.Success;
        InstallerExecutionResult succeeded = await harness.Engine.UninstallRetiredAccountAsync(
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, succeeded.Outcome);
        Assert.Null(harness.Store.Installed);
        Assert.Equal(1, harness.Shell.RemoveCalls);

        harness.Store.Installed = harness.Document(coreVerified: true);
        harness.Shell.RemovalFailure = new UnauthorizedAccessException();
        InstallerExecutionResult degraded = await harness.Engine.UninstallRetiredAccountAsync(
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, degraded.Outcome);
        Assert.True(degraded.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending));
        Assert.NotNull(harness.Store.Installed);
    }

    [Fact]
    public async Task OwnerTransferIsBlockedWhenTheSelectedProfileHasNoServiceOrTheApplicationIsRunning()
    {
        using var noService = new Harness(preferences: true);
        InstallerExecutionResult explicitChoice = await noService.Engine.TransferWithPreferencesAsync(
            InstallerPreferences.Default with { InstallTransparentProxyService = false },
            Accept,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Blocked, explicitChoice.Outcome);
        Assert.Equal("installer.preferences.service_not_selected", explicitChoice.DiagnosticCode);

        noService.Store.Installed = noService.Document(coreVerified: true, preferences: InstallerPreferences.Default with
        {
            InstallTransparentProxyService = false,
        });
        InstallerExecutionResult storedChoice = await noService.Engine.TransferAndExecuteAsync(
            Accept,
            progress: null,
            CancellationToken.None);
        Assert.Equal("installer.preferences.service_not_selected", storedChoice.DiagnosticCode);
        Assert.Equal(0, noService.Factory.Transfers);
        Assert.False(noService.Lock.Held);
        Assert.Equal(0, noService.Store.Stages);

        using var running = new Harness(preferences: true, environment: new InstallerEnvironmentSnapshot(
            true,
            "1.2.3.4",
            IsApplicationRunning: true,
            BlockingDiagnosticCode: null));
        InstallerExecutionResult blocked = await running.Engine.TransferAndExecuteAsync(
            Accept,
            progress: null,
            CancellationToken.None);
        Assert.Equal("installer.application_running", blocked.DiagnosticCode);
        Assert.Equal(0, running.Factory.Transfers);
    }

    [Fact]
    public async Task OwnerTransferForwardsTheConfirmationAndMapsExplicitDeclineToCancelled()
    {
        using var harness = new Harness();
        Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm = Accept;
        InstallerExecutionResult completed = await harness.Engine.TransferAndExecuteAsync(
            confirm,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, completed.Outcome);
        Assert.Same(confirm, harness.Factory.LastConfirm);

        harness.Factory.TransferFailure = new InstallerUserCancelledException("installer.owner_transfer.declined");
        InstallerExecutionResult declined = await harness.Engine.TransferAndExecuteAsync(
            confirm,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Cancelled, declined.Outcome);
        Assert.Equal("installer.owner_transfer.declined", declined.DiagnosticCode);
        Assert.False(harness.Lock.Held);

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Engine.TransferAndExecuteAsync(
            null!,
            progress: null,
            CancellationToken.None));
    }

    [Fact]
    public async Task OwnerTransferWithPreferencesChoosesInstallOrRepairFromTheInstalledPackageAndCompletesUserSetup()
    {
        using var fresh = new Harness(preferences: true);
        InstallerExecutionResult installed = await fresh.Engine.TransferWithPreferencesAsync(
            InstallerPreferences.Default with { EnableMcpServer = true },
            Accept,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, installed.Outcome);
        Assert.NotNull(fresh.Store.Installed);
        Assert.True(fresh.Store.Installed.Preferences.EnableMcpServer);
        Assert.Equal(1, fresh.Setup.Calls);

        using var existing = new Harness(preferences: true, environment: new InstallerEnvironmentSnapshot(
            true,
            "1.2.3.4",
            IsApplicationRunning: false,
            BlockingDiagnosticCode: null));
        InstallerExecutionResult repaired = await existing.Engine.TransferWithPreferencesAsync(
            InstallerPreferences.Default,
            Accept,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, repaired.Outcome);
        Assert.NotNull(existing.Store.Installed);
        Assert.True(existing.Store.Installed.InitializeApplicationDefaults);
    }

    [Fact]
    public async Task LaunchRequiresTheTargetUserBeforeAnyPackageLookupAndReleasesTheGate()
    {
        using var harness = new Harness();

        InstallerProtocolException mismatch = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => harness.Engine.LaunchApplicationAsync(CancellationToken.None));
        Assert.Equal("installer.launch.target_user_mismatch", mismatch.DiagnosticCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Engine.LaunchApplicationAsync(cancellation.Token));

        InstallerExecutionResult after = await harness.Engine.ExecuteAsync(
            InstallerOperation.Repair,
            progress: null,
            CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, after.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task OperationOutsideTheDefinedUniverseIsRejectedBeforeLeaseOrSession(int raw)
    {
        using var harness = new Harness(preferences: true);

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => harness.Engine.ExecuteAsync((InstallerOperation)raw, progress: null, CancellationToken.None));

        Assert.Equal("installer.request.operation_invalid", failure.DiagnosticCode);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Equal(0, harness.Factory.Creates);
        Assert.Equal(0, harness.Inspector.Calls);
    }

    [Fact]
    public async Task EveryDefinedOperationReachesTheSessionWithItsOwnExactRequest()
    {
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            using var harness = new Harness();

            InstallerExecutionResult result = await harness.Engine.ExecuteAsync(
                operation,
                progress: null,
                CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
            Assert.Equal(operation, Assert.Single(harness.Factory.Requests).Operation);
        }
    }

    [Fact]
    public void ExecutionSessionFactoryValidatesItsIdentityBeforeCreatingAnything()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        Func<string?> sid = static () => TargetSid;

        Assert.Equal(
            "installer.release.manifest_missing",
            Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerExecutionSessionFactory(
                ReadOnlyMemory<byte>.Empty,
                fixture.Manifest,
                @"C:\Release\Setup.exe",
                TargetSid,
                sid)).DiagnosticCode);
        Assert.ThrowsAny<ArgumentException>(() => new WindowsInstallerExecutionSessionFactory(
            fixture.ManifestBytes,
            fixture.Manifest,
            "  ",
            TargetSid,
            sid));
        Assert.Throws<ArgumentNullException>(() => new WindowsInstallerExecutionSessionFactory(
            fixture.ManifestBytes,
            fixture.Manifest,
            @"C:\Release\Setup.exe",
            TargetSid,
            null!));
        foreach (string badSid in InvalidTargetSids)
        {
            Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerExecutionSessionFactory(
                fixture.ManifestBytes,
                fixture.Manifest,
                @"C:\Release\Setup.exe",
                badSid,
                sid));
        }
    }

    [Fact]
    public void EngineRefusesAnyTargetOutsideTheOrdinaryUserAccountUniverse()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        foreach (string badSid in InvalidTargetSids)
        {
            Assert.Throws<InstallerProtocolException>(() => WindowsInstallerParentEngine.CreateForTesting(
                fixture.Manifest,
                badSid,
                new PlainEngineSessionFactory(),
                new EngineApplicationLock()));
        }
    }

    [Fact]
    public async Task CancelledSessionCreationBuildsNoResourceAndReportsCancellation()
    {
        using var fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
        var factory = new WindowsInstallerExecutionSessionFactory(
            fixture.ManifestBytes,
            fixture.Manifest,
            Path.Combine(fixture.RootDirectory, "Setup.exe"),
            TargetSid,
            static () => TargetSid);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateAsync(cancellation.Token));
    }

    private static readonly string[] InvalidTargetSids =
    [
        string.Empty, "not-a-sid", "S-1-5-18", "S-1-5-32-544", "S-1-5-7", "S-1-5-2", "S-1-1-0",
        "S-1-5-21-1-2-3-500x", "S-1-5-21-4294967296-2-3", "S-1-5-21-01-2-3", "s-1-5-21-1-2-3", "S-1-5-21--3",
    ];

    private static Task<bool> Accept(InstallerOwnerTransferOffer offer, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    private static IEnumerable<(string Label, Func<Harness, InstallerRuntimeInspection> Inspection)> BlockingInspections()
    {
        yield return ("durable transaction", harness => harness.Inspection(installedVersion: "1.2.3.4", durable: true));
        yield return ("application running", harness => harness.Inspection(installedVersion: "1.2.3.4", running: true));
        yield return ("package not installed", harness => harness.Inspection());
        yield return ("another installed version", harness => harness.Inspection(installedVersion: "1.2.3.3"));
    }

    private static Exception CreateFailure(string kind) => kind switch
    {
        nameof(IOException) => new IOException("io"),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("denied"),
        "Win32Exception5" => new Win32Exception(5),
        "Win32Exception1223" => new Win32Exception(1223),
        nameof(InvalidOperationException) => new InvalidOperationException("state"),
        nameof(TimeoutException) => new TimeoutException("slow"),
        "ComException" => new TestComException("activation", unchecked((int)0x80073D02)),
        "SharingViolation" => new IOException("busy", unchecked((int)0x80070020)),
        "DiskFull" => new IOException("full", unchecked((int)0x80070070)),
        "PathTooLong" => new PathTooLongException("long"),
        nameof(NotSupportedException) => new NotSupportedException("kind"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class Harness : IDisposable
    {
        internal Harness(
            bool preferences = false,
            bool plainFactory = false,
            bool gated = false,
            bool restoringSessions = true,
            InstallerEnvironmentSnapshot? environment = null)
        {
            Fixture = new WindowsPayloadFixture(createPayload: false, removeCurrentUserCertificateOnDispose: false);
            EnvironmentSnapshot = environment ?? new InstallerEnvironmentSnapshot(true, null, false, null);
            Factory = new EngineSessionFactory { Gated = gated, RestoringSessions = restoringSessions };
            Inspector = new EngineInspector(Inspection(environment: EnvironmentSnapshot), Factory);
            WindowsInstallerPreferencesCoordinator? coordinator = preferences
                ? new WindowsInstallerPreferencesCoordinator(Fixture.Manifest, TargetSid, Store, Shell, setup: Setup)
                : null;
            if (plainFactory)
            {
                Plain = new PlainEngineSessionFactory();
            }

            Engine = WindowsInstallerParentEngine.CreateForTesting(
                Fixture.Manifest,
                TargetSid,
                plainFactory ? Plain! : Factory,
                Lock,
                Inspector,
                coordinator);
        }

        internal WindowsPayloadFixture Fixture { get; }

        internal InstallerEnvironmentSnapshot EnvironmentSnapshot { get; }

        internal EngineApplicationLock Lock { get; } = new();

        internal EngineSessionFactory Factory { get; }

        internal PlainEngineSessionFactory? Plain { get; }

        internal EngineInspector Inspector { get; }

        internal EnginePreferencesStore Store { get; } = new();

        internal EnginePreferencesShell Shell { get; } = new();

        internal EnginePreferencesSetup Setup { get; } = new();

        internal WindowsInstallerParentEngine Engine { get; }

        internal InstallerRuntimeInspection Inspection(
            string? installedVersion = null,
            bool running = false,
            bool durable = false,
            InstallerEnvironmentSnapshot? environment = null)
        {
            InstallerTransactionSnapshot? snapshot = durable
                ? InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(
                    Fixture.Request(InstallerOperation.Install, TargetSid)))
                : null;
            return new InstallerRuntimeInspection(
                environment ?? new InstallerEnvironmentSnapshot(true, installedVersion, running, null),
                snapshot,
                Fixture.Manifest.ExpectedPackageVersion);
        }

        internal WindowsInstallerPreferencesDocument Document(
            bool coreVerified,
            InstallerPreferences? preferences = null) => new(
            1,
            Guid.NewGuid().ToString("N"),
            Fixture.Manifest.PackageIdentity.PackageFamilyName,
            Fixture.Manifest.ExpectedPackageVersion,
            Fixture.Manifest.InstallerPayloadSha256,
            preferences ?? InstallerPreferences.Default,
            InitializeApplicationDefaults: true,
            CoreVerified: coreVerified);

        internal Task<InstallerExecutionResult> Start(string name, CancellationToken cancellationToken) => name switch
        {
            "execute" => Engine.ExecuteAsync(InstallerOperation.Install, null, cancellationToken),
            "restore" => Engine.RestoreOriginalAsync(null, cancellationToken),
            "retired" => Engine.UninstallRetiredAccountAsync(null, cancellationToken),
            "transfer" => Engine.TransferAndExecuteAsync(Accept, null, cancellationToken),
            "install-prefs" => Engine.InstallWithPreferencesAsync(InstallerPreferences.Default, null, cancellationToken),
            "transfer-prefs" => Engine.TransferWithPreferencesAsync(
                InstallerPreferences.Default,
                Accept,
                null,
                cancellationToken),
            "complete-prefs" => Engine.CompletePreferencesAsync(null, cancellationToken),
            "inspect" => ProjectAsync(async () => _ = await Engine.InspectAsync(cancellationToken)),
            "launch" => ProjectAsync(() => Engine.LaunchApplicationAsync(cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        /// <summary>Normalizes results and stable protocol failures into one comparable label.</summary>
        internal async Task<string> Observe(string name, CancellationToken cancellationToken)
        {
            try
            {
                InstallerExecutionResult result = await Start(name, cancellationToken);
                return result.Outcome + ":" + result.DiagnosticCode;
            }
            catch (InstallerProtocolException exception)
            {
                return "throws:" + exception.DiagnosticCode;
            }
        }

        public void Dispose()
        {
            Fixture.Dispose();
        }

        private static async Task<InstallerExecutionResult> ProjectAsync(Func<Task> action)
        {
            await action();
            return EngineSessionFactory.Success;
        }
    }
}
