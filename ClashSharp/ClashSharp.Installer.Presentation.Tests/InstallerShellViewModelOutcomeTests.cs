using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

/// <summary>
/// Observable effect of every user-setup warning combination and of retired-account removal progress and
/// failure codes: each distinct fact must reach the user as a distinct state, and nothing may change on failure.
/// </summary>
public sealed class InstallerShellViewModelOutcomeTests
{
    private static readonly InstallerPreferencesWarnings[] Flags = Enum.GetValues<InstallerPreferencesWarnings>()
        .Where(static flag => flag != InstallerPreferencesWarnings.None)
        .ToArray();

    public static TheoryData<InstallerOperation> Operations() => new(
        InstallerOperation.Install, InstallerOperation.Repair, InstallerOperation.Uninstall);

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryWarningCombinationOfASuccessfulOperationIsADistinctObservableOutcome(InstallerOperation operation)
    {
        PreferencesRuntime baselineRuntime = new(operation)
        {
            Result = new InstallerExecutionResult(
                InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false),
        };
        (string Badge, string Title, string Detail) baseline = await Run(baselineRuntime, operation);
        HashSet<(string Badge, string Title, string Detail)> observed = [];
        foreach (int mask in Enumerable.Range(0, 1 << Flags.Length))
        {
            InstallerPreferencesWarnings warnings = Flags.Where((_, index) => (mask & (1 << index)) != 0)
                .Aggregate(InstallerPreferencesWarnings.None, static (all, flag) => all | flag);
            PreferencesRuntime runtime = new(operation)
            {
                Result = new InstallerExecutionResult(
                    InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false)
                {
                    PreferencesWarnings = warnings,
                },
            };
            using InstallerShellViewModel viewModel = new(runtime);
            await viewModel.InitializeAsync();

            await (operation == InstallerOperation.Uninstall
                ? viewModel.SecondaryActionCommand
                : viewModel.PrimaryActionCommand).ExecuteAsync();

            bool pending = warnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending);
            (string, string, string) state = (viewModel.StatusBadge, viewModel.StatusTitle, viewModel.StatusDetail);
            Assert.Equal(pending, viewModel.StatusBadge == "待完成");
            // Pending user setup can be retried only while the package still exists.
            Assert.Equal(pending && operation != InstallerOperation.Uninstall, viewModel.IsCompletePreferencesVisible);
            Assert.False(viewModel.IsBusy);
            // No warning at all leaves the ordinary success untouched; every other set changes what is shown.
            Assert.Equal(mask == 0, state == baseline);
            Assert.True(observed.Add(state), $"Warning set {warnings} is indistinguishable from another.");
        }

        Assert.Equal(1 << Flags.Length, observed.Count);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task WarningsOnANonSuccessfulOutcomeNeverRewriteItsStatus(InstallerOperation operation)
    {
        foreach (InstallerExecutionOutcome outcome in new[]
        {
            InstallerExecutionOutcome.Failed, InstallerExecutionOutcome.Blocked,
            InstallerExecutionOutcome.Uncertain, InstallerExecutionOutcome.Cancelled,
        })
        {
            PreferencesRuntime clean = new(operation) { Result = Failure(outcome, InstallerPreferencesWarnings.None) };
            PreferencesRuntime noisy = new(operation) { Result = Failure(outcome, (InstallerPreferencesWarnings)31) };
            (string Badge, string Title, string Detail) first = await Run(clean, operation);
            (string Badge, string Title, string Detail) second = await Run(noisy, operation);

            Assert.Equal(first, second);
            Assert.NotEqual("待完成", second.Badge);
        }
    }

    [Fact]
    public async Task RetiredAccountRemovalProgressMapsEveryKnownStageAndFallsBackForUnknownOnes()
    {
        string[] known =
        [
            "installer.progress.preflight", "installer.progress.prepared", "installer.progress.machine_prepare",
            "installer.progress.machine_remove_authorize", "installer.progress.machine_remove", "installer.progress.package_remove",
            "installer.progress.certificate_remove", "installer.progress.verifying", "installer.progress.completed",
        ];
        string[] unknown = ["installer.progress.mystery", "installer.progress.certificate", "installer.progress.package"];
        QueuedSynchronizationContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Dictionary<string, string> texts = [];
            InstallerShellViewModel? viewModel = null;
            RetiredRuntime runtime = new(async progress =>
            {
                foreach (string key in known.Concat(unknown))
                {
                    progress.Report(InstallerProgress.Create(InstallerTransactionPhase.Prepared, 10, key));
                    context.Drain();
                    texts[key] = viewModel!.ProgressStatus;
                }

                await Task.CompletedTask;
                return InstallerPresentationTestData.Result(InstallerExecutionOutcome.Succeeded, phase: InstallerTransactionPhase.Verified);
            });
            viewModel = new InstallerShellViewModel(runtime);
            try
            {
                Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
                viewModel.ConfirmRetiredUninstallCommand.Execute(null);
                while (!execution.IsCompleted)
                {
                    context.Drain();
                    // The wait must not resume on the queued context: only this loop drains it, so a
                    // continuation posted there would wait for the loop that is waiting for it.
                    SynchronizationContext.SetSynchronizationContext(previous);
                    await Task.Yield();
                    SynchronizationContext.SetSynchronizationContext(context);
                }

                await execution;
            }
            finally
            {
                viewModel.Dispose();
            }

            Assert.All(texts.Values, static text => Assert.False(string.IsNullOrWhiteSpace(text)));
            // Unknown stages all share the neutral text, and no known stage is mistaken for it.
            Assert.Single(unknown.Select(key => texts[key]).Distinct());
            string neutral = texts[unknown[0]];
            Assert.All(known, key => Assert.NotEqual(neutral, texts[key]));
            // The removal stages describe retired-account work, distinct from the shared-service stages.
            Assert.NotEqual(texts["installer.progress.package_remove"], texts["installer.progress.verifying"]);
            Assert.NotEqual(texts["installer.progress.preflight"], texts["installer.progress.completed"]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Theory]
    [InlineData(InstallerExecutionOutcome.Failed)]
    [InlineData(InstallerExecutionOutcome.Blocked)]
    [InlineData(InstallerExecutionOutcome.Uncertain)]
    public async Task RetiredAccountFailuresExplainKnownCodesDistinctlyAndEveryOtherCodeWithTheRecoveryInstruction(
        InstallerExecutionOutcome outcome)
    {
        string[] known =
        [
            "installer.retired_uninstall.current_owner",
            "installer.retired_uninstall.ordinary_state_pending",
            "installer.owner_transfer.pending",
        ];
        string[] other = ["installer.retired_uninstall.anything", "installer.runtime.execution_failed", "installer.unknown_thing"];
        Dictionary<string, string> details = [];
        foreach (string code in known.Concat(other))
        {
            RetiredRuntime runtime = new(_ => Task.FromResult(new InstallerExecutionResult(outcome, code, null, RecoveryPending: false)));
            using InstallerShellViewModel viewModel = new(runtime);
            Task execution = viewModel.RetiredUninstallCommand.ExecuteAsync();
            viewModel.ConfirmRetiredUninstallCommand.Execute(null);
            await execution.WaitAsync(TestTimeouts.Completion);

            Assert.False(viewModel.IsBusy);
            Assert.Equal(code, viewModel.DiagnosticCode);
            details[code] = viewModel.StatusDetail;
        }

        Assert.Equal(known.Length, known.Select(code => details[code]).Distinct().Count());
        Assert.Single(other.Select(code => details[code]).Distinct());
        Assert.All(known, code => Assert.NotEqual(details[other[0]], details[code]));
    }

    private static InstallerExecutionResult Failure(InstallerExecutionOutcome outcome, InstallerPreferencesWarnings warnings) =>
        new(outcome, "installer.test.result", outcome == InstallerExecutionOutcome.Blocked ? null : InstallerTransactionPhase.Prepared,
            RecoveryPending: outcome != InstallerExecutionOutcome.Blocked)
        {
            PreferencesWarnings = warnings,
        };

    private static async Task<(string Badge, string Title, string Detail)> Run(PreferencesRuntime runtime, InstallerOperation operation)
    {
        using InstallerShellViewModel viewModel = new(runtime);
        await viewModel.InitializeAsync();
        await (operation == InstallerOperation.Uninstall
            ? viewModel.SecondaryActionCommand
            : viewModel.PrimaryActionCommand).ExecuteAsync();
        return (viewModel.StatusBadge, viewModel.StatusTitle, viewModel.StatusDetail);
    }

    private sealed class PreferencesRuntime(InstallerOperation operation) :
        IInstallerRuntime,
        IInstallerPreferencesRuntime
    {
        internal InstallerExecutionResult Result { get; init; } = InstallerPresentationTestData.Result();

        public bool SupportsInstallPreferences => true;

        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(
            operation == InstallerOperation.Install
                ? InstallerPresentationTestData.Readiness()
                : InstallerPresentationTestData.Readiness(
                    InstallerProductState.Installed,
                    allowedOperations: [InstallerOperation.Repair, InstallerOperation.Uninstall]));

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation requested,
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => Task.FromResult(Result);

        public Task<InstallerExecutionResult> InstallWithPreferencesAsync(
            InstallerPreferences preferences,
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => Task.FromResult(Result);

        public Task<InstallerExecutionResult> TransferWithPreferencesAsync(
            InstallerPreferences preferences,
            Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => Task.FromResult(Result);

        public Task<InstallerExecutionResult> CompletePreferencesAsync(
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class RetiredRuntime(Func<IProgress<InstallerProgress>, Task<InstallerExecutionResult>> run) :
        IInstallerRuntime,
        IInstallerRetiredUninstallRuntime
    {
        public bool SupportsRetiredUninstall => true;

        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            Task.FromException<InstallerRuntimeReadiness>(new InstallerProtocolException("installer.transaction.root_verification_failed"));

        public Task<InstallerExecutionResult> ExecuteAsync(
            InstallerOperation operation,
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Ordinary execution is not part of retired removal.");

        public Task<InstallerExecutionResult> UninstallRetiredAccountAsync(
            IProgress<InstallerProgress> progress,
            CancellationToken cancellationToken) => run(progress);
    }
}
