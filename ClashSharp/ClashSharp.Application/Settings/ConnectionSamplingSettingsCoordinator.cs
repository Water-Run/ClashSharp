using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Serializes sampling preference batches with loop quiescence, activation, and compensation.</summary>
public sealed class ConnectionSamplingSettingsCoordinator
{
    private readonly MutationAdmissionBarrier _admission;
    private readonly IConnectionSamplingSettingsOperation _operation;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>Creates a coordinator without reading settings or starting background work.</summary>
    /// <param name="admission">Process-wide admission shared with reset, import, and shutdown.</param>
    /// <param name="operation">Preference and lifecycle operations for the owned sampling loop.</param>
    public ConnectionSamplingSettingsCoordinator(
        MutationAdmissionBarrier admission,
        IConnectionSamplingSettingsOperation operation)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    /// <summary>Acquires ordinary admission and applies one complete preference pair.</summary>
    /// <param name="settings">Requested sampling preferences.</param>
    /// <param name="cancellationToken">Cancels admission and waiting before any lifecycle change.</param>
    public async Task ApplyAsync(ConnectionSamplingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using MutationAdmissionLease lease = await _admission.AcquireOrdinaryAsync(cancellationToken);
        await ApplyAdmittedAsync(_ => settings, lease, cancellationToken);
    }

    /// <summary>Changes the enable preference while retaining the interval observed after serialization.</summary>
    /// <param name="enabled">Requested sampling activation.</param>
    /// <param name="cancellationToken">Cancels only before lifecycle mutation begins.</param>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await _admission.AcquireOrdinaryAsync(cancellationToken);
        await SetEnabledAdmittedAsync(enabled, lease, cancellationToken);
    }

    /// <summary>Applies a durable trigger's enable choice without reacquiring or releasing its admission.</summary>
    /// <param name="enabled">Requested sampling activation.</param>
    /// <param name="admissionLease">Active caller-owned lease kept alive through completion.</param>
    /// <param name="cancellationToken">Cancels only before lifecycle mutation begins.</param>
    public Task SetEnabledAdmittedAsync(
        bool enabled,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken) =>
        ApplyAdmittedAsync(
            baseline => new ConnectionSamplingSettings(enabled, baseline.IntervalSeconds),
            admissionLease,
            cancellationToken);

    private async Task ApplyAdmittedAsync(
        Func<ConnectionSamplingSettings, ConnectionSamplingSettings> selectTarget,
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
            ConnectionSamplingSettings baseline = _operation.ReadSettings();
            bool wasRunning = _operation.IsRunning;
            ConnectionSamplingSettings target = selectTarget(baseline);
            waiting.Token.ThrowIfCancellationRequested();
            try
            {
                // A running iteration must finish before either live preference changes.
                // Once quiescence begins, keep admission until activation or recovery finishes.
                await QuiesceAndVerifyAsync();
                WriteAndVerify(target, admissionLease);
                await ActivateAndVerifyAsync(target.Enabled);
            }
            catch (Exception applicationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(applicationFailure))
            {
                try
                {
                    await QuiesceAndVerifyAsync();
                    WriteAndVerify(baseline, admissionLease);
                    await ActivateAndVerifyAsync(wasRunning);
                }
                catch (Exception recoveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(recoveryFailure))
                {
                    // If quiescence or baseline publication cannot be verified, do not
                    // start a loop against settings whose recovery is still uncertain.
                    throw new ConnectionSamplingSettingsRecoveryException(applicationFailure, recoveryFailure);
                }

                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task QuiesceAndVerifyAsync()
    {
        await _operation.QuiesceAsync(CancellationToken.None);
        if (_operation.IsRunning)
        {
            throw new InvalidOperationException("The sampling loop did not quiesce before preference publication.");
        }
    }

    private void WriteAndVerify(ConnectionSamplingSettings settings, MutationAdmissionLease admissionLease)
    {
        _operation.WriteSettings(settings, admissionLease);
        if (_operation.ReadSettings() != settings)
        {
            throw new InvalidOperationException("The sampling preference batch did not preserve its committed values.");
        }
    }

    private async Task ActivateAndVerifyAsync(bool running)
    {
        if (running)
        {
            await _operation.StartAsync(CancellationToken.None);
        }

        if (_operation.IsRunning != running)
        {
            throw new InvalidOperationException("The sampling loop did not reach its requested activation state.");
        }
    }
}
