using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Execution;

/// <summary>Coordinates one fail-closed, durable package-and-machine transaction at a time.</summary>
public sealed class InstallerCoordinator : IDisposable
{
    private readonly IInstallerEnvironment _environment;
    private readonly IInstallerReleaseVerifier _releaseVerifier;
    private readonly IInstallerCertificateMutation _certificateMutation;
    private readonly IInstallerPackageMutation _packageMutation;
    private readonly IInstallerMachineMutation _machineMutation;
    private readonly IInstallerFinalVerifier _finalVerifier;
    private readonly IInstallerTransactionReader _transactionReader;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private bool _disposed;

    /// <summary>Initializes the coordinator from explicit platform capability ports.</summary>
    public InstallerCoordinator(
        IInstallerEnvironment environment,
        IInstallerReleaseVerifier releaseVerifier,
        IInstallerCertificateMutation certificateMutation,
        IInstallerPackageMutation packageMutation,
        IInstallerMachineMutation machineMutation,
        IInstallerFinalVerifier finalVerifier,
        IInstallerTransactionReader transactionReader)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(releaseVerifier);
        ArgumentNullException.ThrowIfNull(certificateMutation);
        ArgumentNullException.ThrowIfNull(packageMutation);
        ArgumentNullException.ThrowIfNull(machineMutation);
        ArgumentNullException.ThrowIfNull(finalVerifier);
        ArgumentNullException.ThrowIfNull(transactionReader);
        _environment = environment;
        _releaseVerifier = releaseVerifier;
        _certificateMutation = certificateMutation;
        _packageMutation = packageMutation;
        _machineMutation = machineMutation;
        _finalVerifier = finalVerifier;
        _transactionReader = transactionReader;
    }

    /// <summary>Runs or resumes the exact requested transaction.</summary>
    /// <param name="request">Exact user, operation, version, and release hash.</param>
    /// <param name="progress">Optional best-effort presentation observer.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>A sanitized result; incomplete durable work remains resumable.</returns>
    public async Task<InstallerExecutionResult> ExecuteAsync(
        InstallerRequest request,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!_executionGate.Wait(0, CancellationToken.None))
        {
            return Result(
                InstallerExecutionOutcome.Blocked,
                "installer.concurrent_action_rejected",
                null,
                recoveryPending: false);
        }

        InstallerTransactionSnapshot? durable = null;
        bool transactionStateReached = false;
        try
        {
            request.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            ReportSafely(progress, InstallerTransactionPhase.Prepared, 5, "installer.progress.preflight");

            InstallerEnvironmentSnapshot environment = await _environment
                .InspectAsync(request, cancellationToken)
                .ConfigureAwait(false);
            InstallerExecutionResult? environmentBlock = ValidateEnvironment(request, environment);
            if (environmentBlock is not null)
            {
                return environmentBlock;
            }

            await using IInstallerReleaseLease releaseLease = await _releaseVerifier
                .VerifyAsync(request, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.release.lease_missing");
            ValidateRelease(request, releaseLease);

            durable = await _transactionReader.LoadAsync(cancellationToken).ConfigureAwait(false);
            transactionStateReached = true;
            durable?.Validate();
            if (durable is not null && !durable.Journal.Matches(request))
            {
                return Result(
                    InstallerExecutionOutcome.Blocked,
                    "installer.transaction.release_conflict",
                    durable.Journal.Phase,
                    recoveryPending: true);
            }

            if (request.Operation == InstallerOperation.Repair
                && environment.InstalledPackageVersion is null
                && durable is null)
            {
                return Result(
                    InstallerExecutionOutcome.Blocked,
                    "installer.package.repair_requires_installation",
                    null,
                    recoveryPending: false);
            }

            if (durable is null)
            {
                durable = InstallerTransactionSnapshot.Create(
                    InstallerTransactionJournal.Create(request));
                durable.Validate();
            }

            ReportSafely(progress, durable.Journal.Phase, 15, "installer.progress.prepared");
            durable = request.Operation == InstallerOperation.Uninstall
                ? await ExecuteUninstallAsync(
                        request,
                        releaseLease,
                        durable,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await ExecuteInstallOrRepairAsync(
                        request,
                        releaseLease,
                        durable,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (durable.Journal.Phase != InstallerTransactionPhase.Verified)
            {
                await ReverifyReleaseAsync(request, releaseLease, cancellationToken)
                    .ConfigureAwait(false);
                durable.Validate();
                InstallerTransactionSnapshot verified = await _finalVerifier
                    .VerifyAsync(request, releaseLease, durable, cancellationToken)
                    .ConfigureAwait(false);
                durable = ValidateHelperState(
                    durable,
                    verified,
                    InstallerTransactionPhase.Verified);
                durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                    .ConfigureAwait(false);
            }

            ReportSafely(progress, InstallerTransactionPhase.Verified, 96, "installer.progress.verifying");
            await ReverifyReleaseAsync(request, releaseLease, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot confirmed = await _finalVerifier
                .VerifyAsync(request, releaseLease, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                confirmed,
                InstallerTransactionPhase.Verified);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
            InstallerTransactionSnapshot clearReceipt = await _finalVerifier
                .ClearVerifiedAsync(request, releaseLease, durable, cancellationToken)
                .ConfigureAwait(false);
            ValidateClearReceipt(durable, clearReceipt);
            await ConfirmHelperClearAsync(durable, cancellationToken).ConfigureAwait(false);
            ReportSafely(progress, InstallerTransactionPhase.Verified, 100, "installer.progress.completed");
            return Result(
                InstallerExecutionOutcome.Succeeded,
                "installer.completed",
                InstallerTransactionPhase.Verified,
                recoveryPending: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(durable).ConfigureAwait(false);
            }
            return Result(
                InstallerExecutionOutcome.Cancelled,
                "installer.cancelled",
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        catch (InstallerUserCancelledException exception)
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(durable).ConfigureAwait(false);
            }
            return Result(
                InstallerExecutionOutcome.Cancelled,
                exception.DiagnosticCode,
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        catch (HelperStateReloadException exception)
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(exception.FallbackState).ConfigureAwait(false);
            }
            return Result(
                InstallerExecutionOutcome.Uncertain,
                exception.DiagnosticCode,
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        catch (InstallerStateUncertainException exception)
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(durable).ConfigureAwait(false);
            }
            return Result(
                InstallerExecutionOutcome.Uncertain,
                exception.DiagnosticCode,
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        catch (InstallerProtocolException exception)
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(durable).ConfigureAwait(false);
            }
            return Result(
                durable is null ? InstallerExecutionOutcome.Blocked : InstallerExecutionOutcome.Failed,
                exception.DiagnosticCode,
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (transactionStateReached)
            {
                durable = await RefreshDurableAsync(durable).ConfigureAwait(false);
            }
            return Result(
                durable is null ? InstallerExecutionOutcome.Blocked : InstallerExecutionOutcome.Failed,
                "installer.unexpected_failure",
                durable?.Journal.Phase,
                recoveryPending: durable is not null);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>Releases the process-local single-action gate.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _executionGate.Dispose();
        _disposed = true;
    }

    private async Task<InstallerTransactionSnapshot> ExecuteInstallOrRepairAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durable,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (durable.Journal.Phase == InstallerTransactionPhase.Prepared)
        {
            ReportSafely(progress, durable.Journal.Phase, 18, "installer.progress.machine_prepare");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot reserved = await _machineMutation
                .PrepareAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                reserved,
                InstallerTransactionPhase.MachineReserved);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (durable.Journal.Phase == InstallerTransactionPhase.MachineReserved)
        {
            ReportSafely(progress, durable.Journal.Phase, 30, "installer.progress.certificate");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            await _certificateMutation.ApplyAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
            ReportSafely(progress, durable.Journal.Phase, 46, "installer.progress.package");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            await _packageMutation.ApplyAsync(request, release, cancellationToken).ConfigureAwait(false);
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot packageCommitted = await _machineMutation
                .CommitPackageAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                packageCommitted,
                InstallerTransactionPhase.PackageCommitted);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (durable.Journal.Phase == InstallerTransactionPhase.PackageCommitted)
        {
            ReportSafely(progress, durable.Journal.Phase, 68, "installer.progress.machine");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot machineCommitted = await _machineMutation
                .ApplyAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                machineCommitted,
                InstallerTransactionPhase.MachineCommitted);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        return durable;
    }

    private async Task<InstallerTransactionSnapshot> ExecuteUninstallAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durable,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (durable.Journal.Phase == InstallerTransactionPhase.Prepared)
        {
            ReportSafely(
                progress,
                durable.Journal.Phase,
                18,
                "installer.progress.machine_remove_authorize");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot authorized = await _machineMutation
                .PrepareAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                authorized,
                InstallerTransactionPhase.MachineRemovalAuthorized);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (durable.Journal.Phase == InstallerTransactionPhase.MachineRemovalAuthorized)
        {
            ReportSafely(progress, durable.Journal.Phase, 28, "installer.progress.machine_remove");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot machineCommitted = await _machineMutation
                .ApplyAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                machineCommitted,
                InstallerTransactionPhase.MachineCommitted);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (durable.Journal.Phase == InstallerTransactionPhase.MachineCommitted)
        {
            ReportSafely(progress, durable.Journal.Phase, 62, "installer.progress.package_remove");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            await _packageMutation.ApplyAsync(request, release, cancellationToken).ConfigureAwait(false);
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            durable.Validate();
            InstallerTransactionSnapshot packageCommitted = await _machineMutation
                .CommitPackageAsync(request, release, durable, cancellationToken)
                .ConfigureAwait(false);
            durable = ValidateHelperState(
                durable,
                packageCommitted,
                InstallerTransactionPhase.PackageCommitted);
            durable = await ConfirmHelperStateAsync(durable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (durable.Journal.Phase == InstallerTransactionPhase.PackageCommitted)
        {
            ReportSafely(
                progress,
                durable.Journal.Phase,
                78,
                "installer.progress.certificate_remove");
            await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
            await _certificateMutation.ApplyAsync(request, release, cancellationToken)
                .ConfigureAwait(false);
        }

        return durable;
    }

    private async Task ReverifyReleaseAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        CancellationToken cancellationToken) =>
        await release.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);

    private static InstallerTransactionSnapshot ValidateHelperState(
        InstallerTransactionSnapshot current,
        InstallerTransactionSnapshot? helperState,
        InstallerTransactionPhase expectedPhase)
    {
        current.Validate();
        if (helperState is null)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_state_missing");
        }

        helperState.Validate();
        InstallerTransactionSnapshot expected = InstallerTransactionSnapshot.Create(
            current.Journal.TransitionTo(expectedPhase));
        if (helperState != expected)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.result_mismatch");
        }

        return helperState;
    }

    private async Task<InstallerTransactionSnapshot> ConfirmHelperStateAsync(
        InstallerTransactionSnapshot helperState,
        CancellationToken cancellationToken)
    {
        helperState.Validate();
        InstallerTransactionSnapshot? protectedState;
        try
        {
            protectedState = await _transactionReader
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new HelperStateReloadException(
                "installer.transaction.reload_failed",
                helperState);
        }

        if (protectedState is null)
        {
            throw new InstallerStateUncertainException(
                "installer.transaction.helper_state_missing");
        }

        protectedState.Validate();
        if (protectedState != helperState)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.protected_state_mismatch");
        }

        return protectedState;
    }

    private static void ValidateClearReceipt(
        InstallerTransactionSnapshot verifiedState,
        InstallerTransactionSnapshot? clearReceipt)
    {
        verifiedState.Validate();
        if (clearReceipt is null)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.clear_receipt_missing");
        }

        clearReceipt.Validate();
        if (clearReceipt != verifiedState)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.clear_receipt_mismatch");
        }
    }

    private async Task ConfirmHelperClearAsync(
        InstallerTransactionSnapshot verifiedState,
        CancellationToken cancellationToken)
    {
        verifiedState.Validate();
        InstallerTransactionSnapshot? protectedState;
        try
        {
            protectedState = await _transactionReader
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new HelperStateReloadException(
                "installer.transaction.clear_reload_failed",
                verifiedState);
        }

        if (protectedState is not null)
        {
            protectedState.Validate();
            throw new InstallerProtocolException(
                "installer.machine_helper.clear_not_observed");
        }
    }

    private async Task<InstallerTransactionSnapshot?> RefreshDurableAsync(
        InstallerTransactionSnapshot? fallback)
    {
        try
        {
            return await _transactionReader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return fallback;
        }
    }

    private sealed class HelperStateReloadException : Exception
    {
        internal HelperStateReloadException(
            string diagnosticCode,
            InstallerTransactionSnapshot fallbackState)
            : base(diagnosticCode)
        {
            InstallerProtocolValidation.ValidateDiagnosticCode(diagnosticCode);
            fallbackState.Validate();
            DiagnosticCode = diagnosticCode;
            FallbackState = fallbackState;
        }

        internal string DiagnosticCode { get; }

        internal InstallerTransactionSnapshot FallbackState { get; }
    }

    private static InstallerExecutionResult? ValidateEnvironment(
        InstallerRequest request,
        InstallerEnvironmentSnapshot environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // Platform support is an installation target constraint, not a removal constraint.
        // Recovery uninstall must remain available after an OS downgrade or policy change.
        if (!environment.IsSupported && request.Operation != InstallerOperation.Uninstall)
        {
            return Result(
                InstallerExecutionOutcome.Blocked,
                string.IsNullOrWhiteSpace(environment.BlockingDiagnosticCode)
                    ? "installer.environment_unsupported"
                    : environment.BlockingDiagnosticCode,
                null,
                recoveryPending: false);
        }

        if (environment.IsApplicationRunning)
        {
            return Result(
                InstallerExecutionOutcome.Blocked,
                "installer.application_running",
                null,
                recoveryPending: false);
        }

        if (environment.InstalledPackageVersion is { } installedVersion)
        {
            Version installed = InstallerProtocolValidation.ParsePackageVersion(installedVersion);
            Version requested = InstallerProtocolValidation.ParsePackageVersion(request.ExpectedPackageVersion);
            if (request.Operation != InstallerOperation.Uninstall && installed > requested)
            {
                return Result(
                    InstallerExecutionOutcome.Blocked,
                    "installer.package.downgrade_rejected",
                    null,
                    recoveryPending: false);
            }
        }
        return null;
    }

    private static void ValidateRelease(
        InstallerRequest request,
        IInstallerReleaseLease releaseLease)
    {
        ArgumentNullException.ThrowIfNull(releaseLease);
        VerifiedInstallerRelease release = releaseLease.Release
            ?? throw new InstallerProtocolException("installer.release.identity_missing");
        InstallerReleaseManifest manifest = releaseLease.Manifest
            ?? throw new InstallerProtocolException("installer.release.manifest_missing");
        ArgumentNullException.ThrowIfNull(release);
        release.Validate();
        manifest.Validate();
        if (!manifest.Matches(release))
        {
            throw new InstallerProtocolException("installer.release.manifest_identity_mismatch");
        }

        ValidateLockedFileSet(releaseLease, release, manifest);
        if (!string.Equals(
                request.ExpectedPackageVersion,
                release.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                release.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.identity_mismatch");
        }

        if (request.Operation != InstallerOperation.Uninstall && !release.PackagePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.package_payload_missing");
        }

        if (request.Operation != InstallerOperation.Uninstall && !release.CertificatePayloadAvailable)
        {
            throw new InstallerProtocolException("installer.release.certificate_payload_missing");
        }
    }

    private static void ValidateLockedFileSet(
        IInstallerReleaseLease releaseLease,
        VerifiedInstallerRelease release,
        InstallerReleaseManifest manifest)
    {
        IReadOnlyList<IInstallerLockedPayloadFile> lockedFiles = releaseLease.LockedFiles
            ?? throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
        Dictionary<string, InstallerPayloadFileEntry> expected = manifest.Files
            .Where(file => file.Role == InstallerPayloadFileRole.Certificate
                ? release.CertificatePayloadAvailable
                : release.PackagePayloadAvailable)
            .ToDictionary(static file => file.Path, StringComparer.Ordinal);
        if (lockedFiles.Count != expected.Count)
        {
            throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (IInstallerLockedPayloadFile? lockedFile in lockedFiles)
        {
            if (lockedFile?.ManifestEntry is not { } entry
                || string.IsNullOrEmpty(entry.Path)
                || string.IsNullOrWhiteSpace(lockedFile.FullPath)
                || !observed.Add(entry.Path)
                || !expected.TryGetValue(entry.Path, out InstallerPayloadFileEntry? expectedEntry)
                || entry != expectedEntry)
            {
                throw new InstallerProtocolException("installer.release.locked_file_set_invalid");
            }

            entry.Validate();
        }
    }

    private static void ReportSafely(
        IProgress<InstallerProgress>? progress,
        InstallerTransactionPhase phase,
        int percent,
        string messageKey)
    {
        try
        {
            progress?.Report(InstallerProgress.Create(phase, percent, messageKey));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Presentation observers cannot abort a privileged transaction after durable intent.
        }
    }

    private static InstallerExecutionResult Result(
        InstallerExecutionOutcome outcome,
        string diagnosticCode,
        InstallerTransactionPhase? phase,
        bool recoveryPending) =>
        new(outcome, diagnosticCode, phase, recoveryPending);

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
