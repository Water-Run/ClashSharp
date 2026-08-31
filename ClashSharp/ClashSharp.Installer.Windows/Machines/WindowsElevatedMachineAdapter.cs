using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Binds Core machine mutation/final verification to one path-free elevated helper invocation.
/// </summary>
public sealed class WindowsElevatedMachineAdapter :
    IInstallerMachineMutation,
    IInstallerFinalVerifier
{
    private readonly IWindowsMachineHelperBroker _broker;
    private readonly Func<string?> _currentSid;

    internal WindowsElevatedMachineAdapter(
        IWindowsMachineHelperBroker broker,
        Func<string?> currentSid)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(currentSid);
        _broker = broker;
        _currentSid = currentSid;
    }

    /// <inheritdoc />
    public Task<InstallerTransactionSnapshot> PrepareAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            InstallerMachineHelperVerb.Prepare,
            request,
            release,
            durableIntent,
            cancellationToken);

    /// <inheritdoc />
    public Task<InstallerTransactionSnapshot> ApplyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallerMachineHelperVerb verb = request.Operation switch
        {
            InstallerOperation.Install or InstallerOperation.Repair =>
                InstallerMachineHelperVerb.Apply,
            InstallerOperation.Uninstall => InstallerMachineHelperVerb.Remove,
            _ => throw new InstallerProtocolException(
                "installer.machine.operation_invalid"),
        };
        return ExecuteAsync(
            verb,
            request,
            release,
            durableIntent,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<InstallerTransactionSnapshot> CommitPackageAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableIntent,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            InstallerMachineHelperVerb.CommitPackage,
            request,
            release,
            durableIntent,
            cancellationToken);

    /// <inheritdoc />
    public Task<InstallerTransactionSnapshot> VerifyAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            InstallerMachineHelperVerb.Verify,
            request,
            release,
            durableState,
            cancellationToken);

    /// <inheritdoc />
    public Task<InstallerTransactionSnapshot> ClearVerifiedAsync(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot verifiedState,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            InstallerMachineHelperVerb.Clear,
            request,
            release,
            verifiedState,
            cancellationToken);

    private async Task<InstallerTransactionSnapshot> ExecuteAsync(
        InstallerMachineHelperVerb verb,
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState,
        CancellationToken cancellationToken)
    {
        WindowsInstallerReleaseLease windowsLease = ValidateBoundary(
            request,
            release,
            durableState,
            cancellationToken);
        InstallerMachineHelperInvocation invocation =
            InstallerMachineHelperInvocation.Create(verb, durableState);
        InstallerMachineHelperCommand command =
            InstallerMachineHelperCommand.Create(invocation, durableState);

        await windowsLease.ReverifyAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        InstallerMachineHelperResult result;
        try
        {
            // Once ShellExecute accepts the elevation request, do not propagate the UI token.
            // The parent must retain its SafeHandle lease until the helper reports a terminal
            // result or the broker proves termination under its own bounded deadline.
            Task<InstallerMachineHelperResult> operation = _broker.ExecuteAsync(command)
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.operation_missing");
            result = await operation.ConfigureAwait(false)
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.result_missing");
        }
        catch (InstallerUserCancelledException)
        {
            throw;
        }
        catch (InstallerStateUncertainException)
        {
            throw;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.elevation.failed",
                exception);
        }

        InstallerTransactionSnapshot helperState = result.ValidateAgainst(command);
        if (result.Outcome != InstallerMachineHelperOutcome.Succeeded)
        {
            throw new InstallerProtocolException(result.DiagnosticCode);
        }

        return helperState;
    }

    private WindowsInstallerReleaseLease ValidateBoundary(
        InstallerRequest request,
        IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(durableState);
        request.Validate();
        release.Release.Validate();
        release.Manifest.Validate();
        durableState.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            throw new InstallerProtocolException("installer.machine.platform_unsupported");
        }

        if (release is not WindowsInstallerReleaseLease windowsLease
            || !release.Manifest.Matches(release.Release))
        {
            throw new InstallerProtocolException("installer.release.windows_lease_required");
        }

        windowsLease.RequireRequest(request);
        if (!durableState.Journal.Matches(request))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.transaction_mismatch");
        }

        if (!string.Equals(_currentSid(), request.TargetSid, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.machine.target_user_mismatch");
        }

        return windowsLease;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
