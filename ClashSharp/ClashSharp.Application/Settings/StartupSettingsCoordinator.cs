using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Serializes startup changes and owns platform verification, preference commit, and compensation.</summary>
public sealed class StartupSettingsCoordinator
{
    private readonly MutationAdmissionBarrier _admission;
    private readonly IStartupSettingsOperation _operation;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>Creates a coordinator without reading settings or contacting Windows.</summary>
    /// <param name="admission">Process-wide mutation admission shared with reset, import, and shutdown.</param>
    /// <param name="operation">Preference and platform operations for this application lifetime.</param>
    public StartupSettingsCoordinator(MutationAdmissionBarrier admission, IStartupSettingsOperation operation)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    /// <summary>Acquires ordinary admission and awaits one complete startup change.</summary>
    /// <param name="enabled">Desired startup registration and preference.</param>
    /// <param name="cancellationToken">Cancels admission, waiting, and observation before platform mutation.</param>
    public async Task ApplyAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await _admission.AcquireOrdinaryAsync(cancellationToken);
        await ApplyAdmittedAsync(enabled, lease, cancellationToken);
    }

    /// <summary>Uses an existing caller-owned lease, including a durable trigger's admission.</summary>
    /// <param name="enabled">Desired startup registration and preference.</param>
    /// <param name="admissionLease">Active lease kept alive by the caller until this operation completes.</param>
    /// <param name="cancellationToken">Cancels only before platform mutation begins.</param>
    public async Task ApplyAdmittedAsync(
        bool enabled,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        _admission.EnsureActiveLease(admissionLease);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, admissionLease.RevocationToken);
        await _operationGate.WaitAsync(waiting.Token);
        try
        {
            _admission.EnsureActiveLease(admissionLease);
            waiting.Token.ThrowIfCancellationRequested();
            bool baselinePreference = _operation.ReadPreference();
            bool? observed = await _operation.ReadRegistrationAsync(waiting.Token);
            waiting.Token.ThrowIfCancellationRequested();
            bool baselineRegistration = observed
                ?? throw new InvalidOperationException("The current startup registration could not be established.");

            // Once the platform operation starts, retain admission until commit or both
            // compensations finish. A page cancellation cannot leave a half-applied choice.
            try
            {
                await ApplyAndVerifyRegistrationAsync(enabled);
                WriteAndVerifyPreference(enabled, admissionLease);
            }
            catch (Exception applicationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(applicationFailure))
            {
                Exception? recoveryFailure = await RestoreAsync(
                    baselinePreference, baselineRegistration, admissionLease);
                if (recoveryFailure is not null)
                {
                    throw new StartupSettingsRecoveryException(applicationFailure, recoveryFailure);
                }

                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ApplyAndVerifyRegistrationAsync(bool enabled)
    {
        await _operation.ApplyRegistrationAsync(enabled, CancellationToken.None);
        if (await _operation.ReadRegistrationAsync(CancellationToken.None) != enabled)
        {
            throw new InvalidOperationException("Windows did not verify the requested startup registration.");
        }
    }

    private void WriteAndVerifyPreference(bool enabled, MutationAdmissionLease admissionLease)
    {
        _operation.WritePreference(enabled, admissionLease);
        if (_operation.ReadPreference() != enabled)
        {
            throw new InvalidOperationException("The startup preference did not preserve its committed value.");
        }
    }

    private async Task<Exception?> RestoreAsync(
        bool preference,
        bool registration,
        MutationAdmissionLease admissionLease)
    {
        List<Exception> failures = [];
        try
        {
            await ApplyAndVerifyRegistrationAsync(registration);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }

        // Preference restoration remains necessary even when Windows compensation fails.
        // Its baseline can differ from the actual registration observed before the change.
        try
        {
            WriteAndVerifyPreference(preference, admissionLease);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Startup compensation failed for multiple participants.", failures),
        };
    }
}
