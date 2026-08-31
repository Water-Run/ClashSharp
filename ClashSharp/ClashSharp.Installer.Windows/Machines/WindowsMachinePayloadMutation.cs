using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineStagingFile : IAsyncDisposable
{
    Stream Content { get; }

    void FlushToDisk();
}

internal interface IWindowsMachinePayloadSlotNative
{
    void ResetStaging(WindowsMachineDeploymentPlan plan);

    IWindowsMachineStagingFile CreateStagingFile(
        WindowsMachineDeploymentPlan plan,
        WindowsMachinePayloadTarget target);

    void CompleteStagingTree(WindowsMachineDeploymentPlan plan);

    void PromoteStaging(WindowsMachineDeploymentPlan plan);

    void CleanupAfterPromotion(WindowsMachineDeploymentPlan plan);

    void RemoveAllSlots(WindowsMachineDeploymentPlan plan);
}

/// <summary>
/// Stages exact locked-MSIX content before any live-slot mutation, then promotes it with
/// independent filesystem postcondition checks so a lost native acknowledgement is not guessed.
/// </summary>
internal sealed class WindowsMachinePayloadMutation
{
    private readonly IWindowsMachinePayloadSlotNative _native;
    private readonly IWindowsMachinePayloadTreeInspector _inspector;

    internal WindowsMachinePayloadMutation(
        IWindowsMachinePayloadSlotNative native,
        IWindowsMachinePayloadTreeInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(inspector);
        _native = native;
        _inspector = inspector;
    }

    internal async Task StageAsync(
        WindowsMachineDeploymentPlan plan,
        WindowsInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(release);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _native.ResetStaging(plan);
            await using WindowsMachinePayloadArchive archive =
                WindowsMachinePayloadArchive.Open(plan, release, cancellationToken);
            foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using IWindowsMachineStagingFile destination =
                    _native.CreateStagingFile(plan, target);
                await archive.CopyToAsync(
                        target,
                        destination.Content,
                        cancellationToken)
                    .ConfigureAwait(false);
                destination.FlushToDisk();
            }

            _native.CompleteStagingTree(plan);
            RequireStatus(
                plan,
                plan.StagingRoot,
                WindowsMachinePayloadTreeStatus.ExactMatch,
                "installer.machine.payload_staging_verification_failed",
                cancellationToken);
            await release.ReverifyAsync(plan.Request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_staging_failed",
                exception);
        }
    }

    internal void PromoteAndVerify(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsMachinePayloadTreeStatus current = Inspect(
            plan,
            plan.CurrentRoot,
            cancellationToken);
        if (current != WindowsMachinePayloadTreeStatus.ExactMatch)
        {
            RequireStatus(
                plan,
                plan.StagingRoot,
                WindowsMachinePayloadTreeStatus.ExactMatch,
                "installer.machine.payload_staging_missing",
                cancellationToken);
            Exception? failure = TryMutation(() => _native.PromoteStaging(plan));
            if (Inspect(plan, plan.CurrentRoot, cancellationToken)
                != WindowsMachinePayloadTreeStatus.ExactMatch)
            {
                _ = failure;
                throw new InstallerStateUncertainException(
                    "installer.machine.payload_state_uncertain");
            }
        }

        Exception? cleanupFailure = TryMutation(() =>
            _native.CleanupAfterPromotion(plan));
        bool clean = Inspect(plan, plan.StagingRoot, cancellationToken)
                == WindowsMachinePayloadTreeStatus.Missing
            && Inspect(plan, plan.PreviousRoot, cancellationToken)
                == WindowsMachinePayloadTreeStatus.Missing;
        if (!clean)
        {
            _ = cleanupFailure;
            throw new InstallerStateUncertainException(
                "installer.machine.payload_state_uncertain");
        }

        RequireStatus(
            plan,
            plan.CurrentRoot,
            WindowsMachinePayloadTreeStatus.ExactMatch,
            "installer.machine.payload_commit_verification_failed",
            cancellationToken);
    }

    internal void RemoveAndVerify(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = TryMutation(() => _native.RemoveAllSlots(plan));
        bool absent = Inspect(plan, plan.CurrentRoot, cancellationToken)
                == WindowsMachinePayloadTreeStatus.Missing
            && Inspect(plan, plan.StagingRoot, cancellationToken)
                == WindowsMachinePayloadTreeStatus.Missing
            && Inspect(plan, plan.PreviousRoot, cancellationToken)
                == WindowsMachinePayloadTreeStatus.Missing;
        if (!absent)
        {
            _ = failure;
            throw new InstallerStateUncertainException(
                "installer.machine.payload_state_uncertain");
        }
    }

    internal void VerifyInstalled(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        RequireStatus(
            plan,
            plan.CurrentRoot,
            WindowsMachinePayloadTreeStatus.ExactMatch,
            "installer.machine.payload_commit_verification_failed",
            cancellationToken);
        RequireStatus(
            plan,
            plan.StagingRoot,
            WindowsMachinePayloadTreeStatus.Missing,
            "installer.machine.payload_residue_detected",
            cancellationToken);
        RequireStatus(
            plan,
            plan.PreviousRoot,
            WindowsMachinePayloadTreeStatus.Missing,
            "installer.machine.payload_residue_detected",
            cancellationToken);
    }

    private WindowsMachinePayloadTreeStatus Inspect(
        WindowsMachineDeploymentPlan plan,
        string root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WindowsMachinePayloadTreeStatus status = _inspector.Inspect(
                plan,
                root,
                cancellationToken);
            if (!Enum.IsDefined(status))
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_tree_status_invalid");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return status;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_tree_inspection_failed",
                exception);
        }
    }

    private void RequireStatus(
        WindowsMachineDeploymentPlan plan,
        string root,
        WindowsMachinePayloadTreeStatus expected,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        if (Inspect(plan, root, cancellationToken) != expected)
        {
            throw new InstallerProtocolException(diagnosticCode);
        }
    }

    private static Exception? TryMutation(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (InstallerStateUncertainException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return exception;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
