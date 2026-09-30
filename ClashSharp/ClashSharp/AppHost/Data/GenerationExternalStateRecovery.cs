using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Settings;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Captures and independently verifies compensation through one generation's owned native boundaries.</summary>
internal sealed class GenerationExternalStateRecovery(SettingsAuthoritySession session, MutationAdmissionBarrier admission,
    OwnedUiDispatcher dispatcher, IAppearanceNativeSettings appearance, StartupLaunchService startup, INetworkSettingsRuntime network)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SettingsAuthoritySession _session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<GenerationExternalStateSnapshot> CaptureAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        ValidateOwner(lease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwner(lease);
            AppearanceNativeConfiguration observedAppearance = await dispatcher.InvokeAsync(appearance.CaptureConfiguration, cancellationToken).ConfigureAwait(false);
            bool observedStartup = await ReadStartupAsync(cancellationToken).ConfigureAwait(false);
            NetworkSettingsConfiguration observedNetwork = await network.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
            ValidateOwner(lease);
            return new(_session.Generation, observedAppearance, observedStartup, observedNetwork);
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAdmittedAsync(GenerationExternalStateSnapshot snapshot, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateOwner(lease);
        if (!_session.Generation.IsSameGeneration(snapshot.Generation)) { throw new InvalidOperationException("The recovery snapshot belongs to another data generation."); }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwner(lease);
            cancellationToken.ThrowIfCancellationRequested();
            // Once compensation starts, finish every category even after a caller cancels
            // or one native API fails. The caller keeps the transition closed on failure.
            List<Exception> failures = [];
            await CaptureFailureAsync(() => RestoreVerifiedAsync(
                () => network.RestoreConfigurationAsync(snapshot.Network, CancellationToken.None),
                async () => await network.ReadConfigurationAsync(CancellationToken.None).ConfigureAwait(false) == snapshot.Network), failures).ConfigureAwait(false);
            await CaptureFailureAsync(() => RestoreVerifiedAsync(
                () => startup.SetEnabledAsync(snapshot.StartupEnabled, CancellationToken.None),
                async () => await ReadStartupAsync(CancellationToken.None).ConfigureAwait(false) == snapshot.StartupEnabled), failures).ConfigureAwait(false);
            await CaptureFailureAsync(() => RestoreVerifiedAsync(
                () => dispatcher.InvokeAsync(() =>
                {
                    List<Exception> appearanceFailures = [];
                    CaptureFailure(() => appearance.ApplyLanguage(snapshot.Appearance.Language), appearanceFailures);
                    CaptureFailure(() => appearance.ApplyTheme(snapshot.Appearance.Theme), appearanceFailures);
                    CaptureFailure(() => appearance.ApplyAccent(snapshot.Appearance.Accent), appearanceFailures);
                    if (appearanceFailures.Count > 0) { throw new AggregateException(appearanceFailures); }
                    return true;
                }, CancellationToken.None),
                async () => await dispatcher.InvokeAsync(appearance.CaptureConfiguration, CancellationToken.None).ConfigureAwait(false) == snapshot.Appearance), failures).ConfigureAwait(false);
            ValidateOwner(lease);
            if (failures.Count > 0) { throw new AggregateException("The previous external runtime state could not be restored completely.", failures); }
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ReadStartupAsync(CancellationToken cancellationToken) =>
        await startup.TryGetStateAsync(cancellationToken).ConfigureAwait(false) switch
        {
            StartupLaunchTaskState.Enabled => true,
            StartupLaunchTaskState.Disabled => false,
            _ => throw new InvalidOperationException("The actual startup registration is unavailable."),
        };

    private void ValidateOwner(MutationAdmissionLease lease)
    {
        admission.EnsureActiveExclusiveLease(lease);
        _ = _session.Snapshot;
    }

    private static async Task RestoreVerifiedAsync(Func<Task> apply, Func<Task<bool>> verify)
    {
        Exception? applicationFailure = null;
        try { await apply().ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { applicationFailure = failure; }
        try
        {
            if (await verify().ConfigureAwait(false)) { return; }
            throw new InvalidOperationException("The observed native state does not match the captured baseline.");
        }
        catch (Exception verificationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(verificationFailure))
        {
            if (applicationFailure is not null) { throw new AggregateException(applicationFailure, verificationFailure); }
            throw;
        }
    }

    private static async Task CaptureFailureAsync(Func<Task> operation, List<Exception> failures)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { failures.Add(failure); }
    }

    private static void CaptureFailure(Action operation, List<Exception> failures)
    {
        try { operation(); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { failures.Add(failure); }
    }
}
