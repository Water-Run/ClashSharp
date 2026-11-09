using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Execution;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// Admission rules: which request may start, resume or must be refused before any effect, plus
/// the trust checks the parent applies to everything a privileged helper reports back.
/// </summary>
public sealed class InstallerCoordinatorAdmissionTests
{
    private const string OtherSid = "S-1-5-21-100-200-300-1002";

    private static readonly InstallerOperation[] Operations =
        [InstallerOperation.Install, InstallerOperation.Repair, InstallerOperation.Uninstall];

    private static readonly string[] VersionGrid =
    [
        "0.0.0.0", "0.0.0.1", "1.2.3.0", "1.2.3.4", "1.2.3.5", "1.2.3.9", "1.2.3.10", "1.2.4.0",
        "1.10.0.0", "2.0.0.0", "65535.65535.65535.65535",
    ];

    public static TheoryData<InstallerOperation, string, string> VersionPairs()
    {
        var data = new TheoryData<InstallerOperation, string, string>();
        foreach (InstallerOperation operation in Operations)
        {
            foreach (string installed in VersionGrid)
            {
                foreach (string requested in VersionGrid)
                {
                    data.Add(operation, installed, requested);
                }
            }
        }

        return data;
    }

    public static TheoryData<InstallerOperation, string, InstallerTransactionPhase> ConflictingJournals()
    {
        var data = new TheoryData<InstallerOperation, string, InstallerTransactionPhase>();
        foreach (InstallerOperation operation in Operations)
        {
            foreach (string mutation in Mutations(operation).Keys)
            {
                InstallerRequest other = Mutations(operation)[mutation];
                foreach (InstallerTransactionPhase phase in InstallerCoordinatorInterruptionTests.ExpectedChain(other.Operation))
                {
                    data.Add(operation, mutation, phase);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VersionPairs))]
    public async Task DowngradeIsRejectedByNumericVersionOrderAndEverythingElseProceeds(
        InstallerOperation operation,
        string installed,
        string requested)
    {
        VerifiedInstallerRelease release = InstallerTestData.Release() with { ExpectedPackageVersion = requested };
        InstallerScenario scenario = new()
        {
            Release = release,
            Environment = new InstallerEnvironmentSnapshot(true, installed, false, null),
        };
        InstallerRequest request = InstallerTestData.Request(
            operation, allowReassociation: false, version: requested);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(request, null, CancellationToken.None);

        bool downgrade = operation != InstallerOperation.Uninstall && Compare(installed, requested) > 0;
        if (downgrade)
        {
            Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
            Assert.Equal("installer.package.downgrade_rejected", result.DiagnosticCode);
            Assert.False(result.RecoveryPending);
            Assert.Null(scenario.Store.Current);
            Assert.DoesNotContain(
                scenario.Events,
                static entry => entry.StartsWith("journal.save:", StringComparison.Ordinal)
                    || entry.StartsWith("package.apply", StringComparison.Ordinal)
                    || entry.StartsWith("certificate.apply", StringComparison.Ordinal)
                    || entry.StartsWith("machine.", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.65536")]
    [InlineData("a.b.c.d")]
    [InlineData("1.2.3.-1")]
    [InlineData(" 1.2.3.4")]
    [InlineData("1.2.3.4 ")]
    [InlineData("1..3.4")]
    [InlineData("١.٢.٣.٤")]
    public async Task UnparseableInstalledVersionFailsClosedBeforeAnyEffectForEveryOperation(string installed)
    {
        foreach (InstallerOperation operation in Operations)
        {
            InstallerScenario scenario = new()
            {
                Environment = new InstallerEnvironmentSnapshot(true, installed, false, null),
            };
            using InstallerCoordinator coordinator = scenario.CreateCoordinator();

            InstallerExecutionResult result = await coordinator.ExecuteAsync(
                InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
            Assert.Equal("installer.request.package_version_invalid", result.DiagnosticCode);
            Assert.False(result.RecoveryPending);
            Assert.Equal(["environment.inspect"], scenario.Events);
        }
    }

    [Theory]
    [InlineData(InstallerOperation.Install, false, "installer.environment_unsupported", "installer.environment_unsupported")]
    [InlineData(InstallerOperation.Repair, false, "installer.environment.windows_11_required", "installer.environment.windows_11_required")]
    [InlineData(InstallerOperation.Install, false, "", "installer.environment_unsupported")]
    [InlineData(InstallerOperation.Repair, false, "   ", "installer.environment_unsupported")]
    [InlineData(InstallerOperation.Install, false, null, "installer.environment_unsupported")]
    public async Task UnsupportedEnvironmentBlocksInstallAndRepairWithItsOwnDiagnostic(
        InstallerOperation operation,
        bool supported,
        string? blockingCode,
        string expectedCode)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        scenario.Environment = new InstallerEnvironmentSnapshot(
            supported, scenario.Environment.InstalledPackageVersion, false, blockingCode);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.False(result.RecoveryPending);
        Assert.Equal(["environment.inspect"], scenario.Events);
        Assert.Null(scenario.Store.Current);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task RunningApplicationBlocksEveryOperationIncludingUninstallBeforeAnyEffect(InstallerOperation operation)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        scenario.Environment = scenario.Environment with { IsApplicationRunning = true };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.application_running", result.DiagnosticCode);
        Assert.Equal(["environment.inspect"], scenario.Events);
    }

    [Fact]
    public async Task RepairWithoutAnyInstallationOrJournalIsBlockedButAPendingRepairJournalMayResume()
    {
        InstallerScenario scenario = new();
        using (InstallerCoordinator coordinator = scenario.CreateCoordinator())
        {
            InstallerExecutionResult blocked = await coordinator.ExecuteAsync(
                InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Repair), null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Blocked, blocked.Outcome);
            Assert.Equal("installer.package.repair_requires_installation", blocked.DiagnosticCode);
            Assert.Null(scenario.Store.Current);
            Assert.DoesNotContain(scenario.Events, static entry => entry.StartsWith("machine.", StringComparison.Ordinal));
        }

        InstallerScenario resumable = new(JournalAt(
            InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Repair),
            InstallerTransactionPhase.PackageCommitted));
        using InstallerCoordinator resumed = resumable.CreateCoordinator();
        InstallerExecutionResult result = await resumed.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Repair), null, CancellationToken.None);
        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(ConflictingJournals))]
    public async Task APendingJournalOfADifferentIdentityBlocksTheRequestAndIsNeverTouched(
        InstallerOperation operation,
        string mutation,
        InstallerTransactionPhase phase)
    {
        InstallerRequest requested = InstallerCoordinatorInterruptionTests.RequestFor(operation);
        InstallerRequest pendingRequest = Mutations(operation)[mutation];
        InstallerTransactionJournal pending = JournalAt(pendingRequest, phase);
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation, pending);
        InstallerTransactionSnapshot before = InstallerTransactionSnapshot.Create(pending);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(requested, null, CancellationToken.None);

        Assert.False(pending.Matches(requested));
        Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("installer.transaction.release_conflict", result.DiagnosticCode);
        Assert.True(result.RecoveryPending);
        Assert.Equal(phase, result.LastDurablePhase);
        Assert.Equal(before, scenario.Store.Current);
        Assert.Equal(
            ["environment.inspect", "release.verify", "journal.load", "release.dispose"],
            scenario.Events);
    }

    [Theory]
    [InlineData(InstallerOperation.Install)]
    [InlineData(InstallerOperation.Repair)]
    [InlineData(InstallerOperation.Uninstall)]
    public async Task ACorruptedDurableJournalIsRejectedFailClosedWithoutOverwritingIt(InstallerOperation operation)
    {
        InstallerRequest request = InstallerCoordinatorInterruptionTests.RequestFor(operation);
        InstallerTransactionJournal valid = JournalAt(request, InstallerTransactionPhase.Prepared);
        InstallerTransactionJournal[] invalid =
        [
            valid with { Generation = 2 },
            valid with { Schema = 3 },
            valid with { TransactionId = "ABC" },
            valid with { Phase = (InstallerTransactionPhase)99 },
            valid with { TargetSid = "S-1-5-18" },
        ];
        foreach (InstallerTransactionJournal corrupt in invalid)
        {
            InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
            // The reader is what the parent trusts; hand it a snapshot built without validation.
            InstallerTransactionSnapshot forged = new(corrupt, InstallerTestData.Hash);
            scenario.Store.LoadAction = _ => Task.FromResult<InstallerTransactionSnapshot?>(forged);
            using InstallerCoordinator coordinator = scenario.CreateCoordinator();

            InstallerExecutionResult result = await coordinator.ExecuteAsync(request, null, CancellationToken.None);

            Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
            Assert.True(
                result.DiagnosticCode.StartsWith("installer.transaction.", StringComparison.Ordinal)
                    || result.DiagnosticCode.StartsWith("installer.request.", StringComparison.Ordinal),
                result.DiagnosticCode);
            Assert.DoesNotContain(
                scenario.Events,
                static entry => entry.StartsWith("machine.", StringComparison.Ordinal)
                    || entry.StartsWith("package.apply", StringComparison.Ordinal)
                    || entry.StartsWith("certificate.apply", StringComparison.Ordinal));
            Assert.Null(scenario.Store.Current);
        }
    }

    [Theory]
    [InlineData("lease_null")]
    [InlineData("release_null")]
    [InlineData("manifest_null")]
    [InlineData("manifest_invalid")]
    [InlineData("manifest_release_mismatch")]
    [InlineData("locked_files_null")]
    [InlineData("locked_file_missing")]
    [InlineData("locked_file_extra")]
    [InlineData("locked_file_duplicate")]
    [InlineData("locked_file_null")]
    [InlineData("locked_file_foreign_entry")]
    [InlineData("locked_file_blank_path")]
    [InlineData("request_version_mismatch")]
    [InlineData("request_hash_mismatch")]
    public async Task AnyInconsistentReleaseLeaseIsRefusedBeforeDurableStateOrEffects(string defect)
    {
        foreach (InstallerOperation operation in Operations)
        {
            InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
            InstallerRequest request = InstallerCoordinatorInterruptionTests.RequestFor(operation);
            ReleaseLeaseDefect lease = ReleaseLeaseDefect.Create(defect, operation);
            if (defect == "request_version_mismatch")
            {
                request = request with { ExpectedPackageVersion = "1.2.3.5" };
            }

            if (defect == "request_hash_mismatch")
            {
                request = request with { InstallerPayloadSha256 = InstallerTestData.OtherHash };
            }

            using InstallerCoordinator coordinator = new(
                scenario, lease.Verifier, scenario, scenario, scenario, scenario, scenario.Store);

            InstallerExecutionResult result = await coordinator.ExecuteAsync(request, null, CancellationToken.None);

            Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
            Assert.StartsWith("installer.release.", result.DiagnosticCode, StringComparison.Ordinal);
            Assert.False(result.RecoveryPending);
            Assert.Null(scenario.Store.Current);
            Assert.DoesNotContain(
                scenario.Events,
                static entry => entry.StartsWith("journal.", StringComparison.Ordinal)
                    || entry.StartsWith("machine.", StringComparison.Ordinal)
                    || entry.StartsWith("package.apply", StringComparison.Ordinal)
                    || entry.StartsWith("certificate.apply", StringComparison.Ordinal));
            Assert.True(lease.Disposed || defect == "lease_null");
        }
    }

    [Theory]
    [InlineData(true, true, InstallerOperation.Install, "installer.release.certificate_payload_missing")]
    [InlineData(false, false, InstallerOperation.Install, "installer.release.package_payload_missing")]
    [InlineData(true, true, InstallerOperation.Repair, "installer.release.certificate_payload_missing")]
    [InlineData(false, false, InstallerOperation.Repair, "installer.release.package_payload_missing")]
    public async Task InstallAndRepairNeedBothVerifiedPayloadsButUninstallNeedsNeither(
        bool packageAvailable,
        bool certificateMissing,
        InstallerOperation operation,
        string expectedCode)
    {
        bool certificateAvailable = !certificateMissing;
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        scenario.Release = InstallerTestData.Release(packageAvailable, certificateAvailable);
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult blocked = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Blocked, blocked.Outcome);
        Assert.Equal(expectedCode, blocked.DiagnosticCode);

        InstallerScenario uninstall = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Uninstall);
        uninstall.Release = InstallerTestData.Release(packageAvailable, certificateAvailable);
        using InstallerCoordinator uninstaller = uninstall.CreateCoordinator();
        Assert.Equal(
            InstallerExecutionOutcome.Succeeded,
            (await uninstaller.ExecuteAsync(
                InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Uninstall),
                null,
                CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(InstallerOperation.Install, "prepare")]
    [InlineData(InstallerOperation.Install, "commit_package")]
    [InlineData(InstallerOperation.Install, "apply")]
    [InlineData(InstallerOperation.Install, "final")]
    [InlineData(InstallerOperation.Repair, "prepare")]
    [InlineData(InstallerOperation.Repair, "commit_package")]
    [InlineData(InstallerOperation.Repair, "apply")]
    [InlineData(InstallerOperation.Repair, "final")]
    [InlineData(InstallerOperation.Uninstall, "prepare")]
    [InlineData(InstallerOperation.Uninstall, "commit_package")]
    [InlineData(InstallerOperation.Uninstall, "apply")]
    [InlineData(InstallerOperation.Uninstall, "final")]
    public async Task EveryHelperBoundaryRejectsAnUntrustworthyReceiptAndNeverAdvancesOnIt(
        InstallerOperation operation,
        string boundary)
    {
        foreach (string defect in new[] { "null", "other_transaction", "skipped_phase", "stale", "forged_hash" })
        {
            if (defect == "skipped_phase" && boundary == "final")
            {
                continue; // Nothing follows Verified, so a skipped phase cannot be expressed here.
            }

            InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
            Func<InstallerTransactionSnapshot, InstallerTransactionSnapshot> factory = intent => defect switch
            {
                "null" => intent,
                "other_transaction" => InstallerTransactionSnapshot.Create(
                    intent.Journal.TransitionTo(NextPhase(intent.Journal)) with { TransactionId = InstallerTestData.OtherHash }),
                "skipped_phase" => InstallerTransactionSnapshot.Create(
                    intent.Journal.TransitionTo(NextPhase(intent.Journal)).TransitionTo(
                        NextPhase(intent.Journal.TransitionTo(NextPhase(intent.Journal))))),
                "stale" => intent,
                _ => new InstallerTransactionSnapshot(
                    intent.Journal.TransitionTo(NextPhase(intent.Journal)), InstallerTestData.OtherHash),
            };
            switch (boundary)
            {
                case "prepare":
                    scenario.MachinePrepareResultFactory = factory;
                    break;
                case "commit_package":
                    scenario.PackageCommitResultFactory = factory;
                    break;
                case "apply":
                    scenario.MachineResultFactory = factory;
                    break;
                default:
                    scenario.FinalResultFactory = factory;
                    break;
            }

            NullReceiptPorts ports = new(scenario, defect == "null" ? boundary : null);
            using InstallerCoordinator coordinator = new(
                scenario, scenario, scenario, scenario, ports, ports, scenario.Store);
            InstallerExecutionResult result = await coordinator.ExecuteAsync(
                InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

            Assert.True(result.Outcome == InstallerExecutionOutcome.Failed, $"{defect}: {result.Outcome} {result.DiagnosticCode}");
            Assert.True(
                result.DiagnosticCode is "installer.machine_helper.result_mismatch"
                    or "installer.machine_helper.result_state_missing"
                    or "installer.transaction.content_hash_mismatch",
                result.DiagnosticCode);
            Assert.True(result.RecoveryPending);
            // The parent adopted nothing from the bad receipt: only what the helper durably committed counts.
            Assert.Equal(scenario.Store.Current?.Journal.Phase, result.LastDurablePhase);
            Assert.DoesNotContain("journal.clear", scenario.Events);
            // Only the final-verification boundary ever reaches the Verified journal write.
            Assert.Equal(boundary == "final", scenario.Events.Contains("journal.save:Verified"));
        }
    }

    [Theory]
    [InlineData(InstallerOperation.Install, "after_prepare_missing")]
    [InlineData(InstallerOperation.Uninstall, "after_prepare_missing")]
    [InlineData(InstallerOperation.Install, "after_prepare_stale")]
    [InlineData(InstallerOperation.Uninstall, "after_prepare_stale")]
    public async Task ProtectedStoreMustAgreeWithTheHelperBeforeTheParentContinues(
        InstallerOperation operation,
        string scenarioName)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        InstallerTransactionSnapshot? stale = null;
        int loads = 0;
        scenario.Store.LoadAction = _ =>
        {
            loads++;
            if (loads == 1)
            {
                return Task.FromResult<InstallerTransactionSnapshot?>(null);
            }

            stale ??= InstallerTransactionSnapshot.Create(
                InstallerTransactionJournal.Create(InstallerCoordinatorInterruptionTests.RequestFor(operation)));
            return Task.FromResult<InstallerTransactionSnapshot?>(scenarioName.EndsWith("missing", StringComparison.Ordinal)
                ? null
                : stale);
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        bool missing = scenarioName.EndsWith("missing", StringComparison.Ordinal);
        Assert.Equal(missing ? InstallerExecutionOutcome.Uncertain : InstallerExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(
            missing ? "installer.transaction.helper_state_missing" : "installer.machine_helper.protected_state_mismatch",
            result.DiagnosticCode);
        Assert.DoesNotContain(
            scenario.Events,
            static entry => entry.StartsWith("package.apply", StringComparison.Ordinal)
                || entry.StartsWith("certificate.apply", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallerOperation.Install, "null_receipt", "installer.machine_helper.clear_receipt_missing")]
    [InlineData(InstallerOperation.Uninstall, "null_receipt", "installer.machine_helper.clear_receipt_missing")]
    [InlineData(InstallerOperation.Install, "null_state", "installer.machine_helper.clear_receipt_missing")]
    [InlineData(InstallerOperation.Repair, "other_state", "installer.machine_helper.clear_receipt_mismatch")]
    [InlineData(InstallerOperation.Uninstall, "other_state", "installer.machine_helper.clear_receipt_mismatch")]
    [InlineData(InstallerOperation.Install, "cleanup_on_non_uninstall", "installer.directory_cleanup.result_binding_invalid")]
    [InlineData(InstallerOperation.Repair, "cleanup_on_non_uninstall", "installer.directory_cleanup.result_binding_invalid")]
    public async Task ClearReceiptMustProveTheExactVerifiedStateAndNeverReportSuccessOtherwise(
        InstallerOperation operation,
        string defect,
        string expectedCode)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(operation);
        ClearReceiptOverride final = new(scenario, defect);
        using InstallerCoordinator coordinator = new(
            scenario, scenario, scenario, scenario, scenario, final, scenario.Store);

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(operation), null, CancellationToken.None);

        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Null(result.DirectoryCleanupReport);
    }

    [Fact]
    public async Task OnlyAnOrdinaryUninstallMayCarryAVerifiedCleanupReportAndItIsForwarded()
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Uninstall);
        InstallerDirectoryCleanupReport report = new(
            Enum.GetValues<InstallerDirectoryRole>().Select(static role =>
                new InstallerDirectoryCleanupEntry(
                    role,
                    role == InstallerDirectoryRole.ProgramDataProduct
                        ? InstallerDirectoryCleanupDisposition.RetainedNonEmpty
                        : InstallerDirectoryCleanupDisposition.Deleted)));
        scenario.DirectoryCleanupReport = report;
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Uninstall), null, CancellationToken.None);

        Assert.Equal(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(report, result.DirectoryCleanupReport);
        Assert.True(result.DirectoryCleanupReport!.HasRetained);
    }

    [Theory]
    [InlineData("still_present")]
    [InlineData("reload_failed")]
    public async Task TerminalClearMustBeObservedAndAFailedObservationIsNeverSuccess(string defect)
    {
        InstallerScenario scenario = InstallerCoordinatorInterruptionTests.NewScenario(InstallerOperation.Install);
        InstallerTransactionSnapshot? leftBehind = null;
        int loads = 0;
        scenario.Store.LoadAction = _ =>
        {
            loads++;
            // 1 initial, 4 phase confirmations (reserve, package, machine, verified), then the
            // pre-clear confirmation, then the post-clear absence check.
            if (loads <= 6)
            {
                return Task.FromResult<InstallerTransactionSnapshot?>(scenario.Store.Current);
            }

            if (defect == "reload_failed")
            {
                return Task.FromException<InstallerTransactionSnapshot?>(new IOException("secret-store-path"));
            }

            return Task.FromResult<InstallerTransactionSnapshot?>(leftBehind ??= InstallerTransactionSnapshot.Create(
                InstallerTestData.Journal() with { Phase = InstallerTransactionPhase.Verified, Generation = 5 }));
        };
        using InstallerCoordinator coordinator = scenario.CreateCoordinator();

        InstallerExecutionResult result = await coordinator.ExecuteAsync(
            InstallerCoordinatorInterruptionTests.RequestFor(InstallerOperation.Install), null, CancellationToken.None);

        Assert.NotEqual(InstallerExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            defect == "reload_failed" ? "installer.transaction.clear_reload_failed" : "installer.machine_helper.clear_not_observed",
            result.DiagnosticCode);
    }

    private static Dictionary<string, InstallerRequest> Mutations(InstallerOperation operation)
    {
        InstallerRequest baseline = InstallerCoordinatorInterruptionTests.RequestFor(operation);
        var mutations = new Dictionary<string, InstallerRequest>
        {
            ["other_account"] = baseline with { TargetSid = OtherSid },
            ["other_version"] = baseline with { ExpectedPackageVersion = "1.2.3.5" },
            ["older_version"] = baseline with { ExpectedPackageVersion = "1.2.3.3" },
            ["other_payload"] = baseline with { InstallerPayloadSha256 = InstallerTestData.OtherHash },
        };
        foreach (InstallerOperation other in Operations.Where(candidate => candidate != operation))
        {
            mutations[$"operation_{other}"] = InstallerCoordinatorInterruptionTests.RequestFor(other);
        }

        if (operation == InstallerOperation.Repair)
        {
            mutations["no_reassociation"] = baseline with { AllowReassociation = false };
        }

        mutations["service_profile"] = baseline with
        {
            InstallTransparentProxyService = false,
            AllowReassociation = false,
        };
        return mutations;
    }

    private static InstallerTransactionJournal JournalAt(InstallerRequest request, InstallerTransactionPhase target)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request);
        foreach (InstallerTransactionPhase phase in InstallerCoordinatorInterruptionTests.ExpectedChain(request.Operation).Skip(1))
        {
            if (journal.Phase == target)
            {
                break;
            }

            journal = journal.TransitionTo(phase);
        }

        Assert.Equal(target, journal.Phase);
        return journal;
    }

    private static InstallerTransactionPhase NextPhase(InstallerTransactionJournal journal)
    {
        InstallerTransactionPhase[] chain = InstallerCoordinatorInterruptionTests.ExpectedChain(journal.Operation);
        return chain[Array.IndexOf(chain, journal.Phase) + 1];
    }

    private static int Compare(string left, string right)
    {
        int[] a = left.Split('.').Select(int.Parse).ToArray();
        int[] b = right.Split('.').Select(int.Parse).ToArray();
        for (int index = 0; index < a.Length; index++)
        {
            if (a[index] != b[index])
            {
                return a[index].CompareTo(b[index]);
            }
        }

        return 0;
    }

    private sealed class ReleaseLeaseDefect
    {
        private ReleaseLeaseDefect(IInstallerReleaseLease? lease)
        {
            Lease = lease;
            Verifier = new FixedVerifier(this);
        }

        internal IInstallerReleaseLease? Lease { get; }

        internal IInstallerReleaseVerifier Verifier { get; }

        internal bool Disposed => (Lease as StubLease)?.Disposed ?? false;

        internal static ReleaseLeaseDefect Create(string defect, InstallerOperation operation)
        {
            VerifiedInstallerRelease release = InstallerTestData.Release();
            InstallerReleaseManifest manifest = InstallerTestData.Manifest(release);
            IReadOnlyList<IInstallerLockedPayloadFile> locked = manifest.Files
                .Where(file => file.Role == InstallerPayloadFileRole.Certificate
                    ? release.CertificatePayloadAvailable
                    : release.PackagePayloadAvailable)
                .Select(static file => (IInstallerLockedPayloadFile)new TestLockedPayloadFile(file))
                .ToArray();
            _ = operation;
            switch (defect)
            {
                case "lease_null":
                    return new ReleaseLeaseDefect(null);
                case "release_null":
                    return Of(null!, manifest, locked);
                case "manifest_null":
                    return Of(release, null!, locked);
                case "manifest_invalid":
                    return Of(
                        release,
                        new InstallerReleaseManifest(
                            7, manifest.ExpectedPackageVersion, manifest.InstallerPayloadSha256,
                            manifest.AuthenticodeCertificateThumbprint, manifest.PackageCertificateThumbprint,
                            manifest.CertificateSha256, manifest.PackageIdentity, manifest.Dependencies,
                            manifest.MachineFiles, manifest.Files),
                        locked);
                case "manifest_release_mismatch":
                    return Of(release with { CertificateSha256 = InstallerTestData.OtherHash }, manifest, locked);
                case "locked_files_null":
                    return Of(release, manifest, null!);
                case "locked_file_missing":
                    return Of(release, manifest, locked.Skip(1).ToArray());
                case "locked_file_extra":
                    return Of(
                        release,
                        manifest,
                        [.. locked, new TestLockedPayloadFile(new InstallerPayloadFileEntry(
                            "unexpected.msix", InstallerPayloadFileRole.PrimaryPackage, 1, InstallerTestData.Hash))]);
                case "locked_file_duplicate":
                    return Of(release, manifest, [.. locked.Skip(1), locked[1]]);
                case "locked_file_null":
                    return Of(release, manifest, [.. locked.Skip(1), null!]);
                case "locked_file_foreign_entry":
                    return Of(
                        release,
                        manifest,
                        [.. locked.Skip(1), new TestLockedPayloadFile(locked[0].ManifestEntry with { Length = locked[0].ManifestEntry.Length + 1 })]);
                case "locked_file_blank_path":
                    return Of(release, manifest, [.. locked.Skip(1), new BlankPathFile(locked[0].ManifestEntry)]);
                default:
                    return Of(release, manifest, locked);
            }
        }

        private static ReleaseLeaseDefect Of(
            VerifiedInstallerRelease release,
            InstallerReleaseManifest manifest,
            IReadOnlyList<IInstallerLockedPayloadFile> locked) =>
            new(new StubLease(release, manifest, locked));

        private sealed class FixedVerifier(ReleaseLeaseDefect owner) : IInstallerReleaseVerifier
        {
            public Task<IInstallerReleaseLease> VerifyAsync(InstallerRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Lease!);
        }
    }

    private sealed class StubLease(
        VerifiedInstallerRelease release,
        InstallerReleaseManifest manifest,
        IReadOnlyList<IInstallerLockedPayloadFile> lockedFiles) : IInstallerReleaseLease
    {
        public VerifiedInstallerRelease Release { get; } = release;

        public InstallerReleaseManifest Manifest { get; } = manifest;

        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; } = lockedFiles;

        internal bool Disposed { get; private set; }

        public Task ReverifyAsync(InstallerRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlankPathFile(InstallerPayloadFileEntry entry) : IInstallerLockedPayloadFile
    {
        public InstallerPayloadFileEntry ManifestEntry { get; } = entry;

        public string FullPath => "  ";
    }

    /// <summary>Delegates to the scenario but returns a missing receipt at one named boundary.</summary>
    private sealed class NullReceiptPorts(InstallerScenario inner, string? nullBoundary) :
        IInstallerMachineMutation,
        IInstallerFinalVerifier
    {
        public async Task<InstallerTransactionSnapshot> PrepareAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot real = await ((IInstallerMachineMutation)inner)
                .PrepareAsync(request, release, durableIntent, cancellationToken);
            return nullBoundary == "prepare" ? null! : real;
        }

        public async Task<InstallerTransactionSnapshot> CommitPackageAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot real = await ((IInstallerMachineMutation)inner)
                .CommitPackageAsync(request, release, durableIntent, cancellationToken);
            return nullBoundary == "commit_package" ? null! : real;
        }

        public async Task<InstallerTransactionSnapshot> ApplyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableIntent,
            CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot real = await ((IInstallerMachineMutation)inner)
                .ApplyAsync(request, release, durableIntent, cancellationToken);
            return nullBoundary == "apply" ? null! : real;
        }

        public async Task<InstallerTransactionSnapshot> VerifyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableState,
            CancellationToken cancellationToken)
        {
            InstallerTransactionSnapshot real = await inner.VerifyAsync(request, release, durableState, cancellationToken);
            return nullBoundary == "final" ? null! : real;
        }

        public Task<InstallerClearReceipt> ClearVerifiedAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot verifiedState,
            CancellationToken cancellationToken) =>
            inner.ClearVerifiedAsync(request, release, verifiedState, cancellationToken);
    }

    private sealed class ClearReceiptOverride(InstallerScenario inner, string defect) : IInstallerFinalVerifier
    {
        public Task<InstallerTransactionSnapshot> VerifyAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot durableState,
            CancellationToken cancellationToken) =>
            inner.VerifyAsync(request, release, durableState, cancellationToken);

        public async Task<InstallerClearReceipt> ClearVerifiedAsync(
            InstallerRequest request,
            IInstallerReleaseLease release,
            InstallerTransactionSnapshot verifiedState,
            CancellationToken cancellationToken)
        {
            InstallerClearReceipt real = await inner.ClearVerifiedAsync(request, release, verifiedState, cancellationToken);
            switch (defect)
            {
                case "null_receipt":
                    return null!;
                case "null_state":
                    return new InstallerClearReceipt(null!);
                case "other_state":
                    return new InstallerClearReceipt(InstallerTransactionSnapshot.Create(
                        verifiedState.Journal with { TransactionId = InstallerTestData.OtherHash }));
                default:
                    return real with
                    {
                        DirectoryCleanupReport = new InstallerDirectoryCleanupReport(
                            Enum.GetValues<InstallerDirectoryRole>().Select(static role =>
                                new InstallerDirectoryCleanupEntry(role, InstallerDirectoryCleanupDisposition.Missing))),
                    };
            }
        }
    }
}
