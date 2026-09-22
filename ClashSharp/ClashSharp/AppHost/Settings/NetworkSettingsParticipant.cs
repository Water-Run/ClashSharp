using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Applies explicit network batches under the settings authority's existing admission.</summary>
internal sealed class NetworkSettingsParticipant : ISettingsApplicationParticipant, IAsyncDisposable
{
    private readonly SettingsParticipantBinding _binding;
    private readonly MutationAdmissionBarrier _admission;
    private readonly INetworkSettingsRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private int _retiring;
    private Task? _retirement;
    private Guid? _failedBatchId;
    private readonly HashSet<Guid> _failedAttempts = [];

    public NetworkSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission, INetworkSettingsRuntime runtime)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _binding = new(generation, admission, SettingApplicationKind.Network, SettingsRegistry.Keys.CurrentMode,
            SettingsRegistry.Keys.ActiveProfileId, SettingsRegistry.Keys.TransparentProxyEnabled, SettingsRegistry.Keys.MixedPort);
    }

    public SettingApplicationKind ApplicationKind => SettingApplicationKind.Network;

    public async Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(request, admissionLease);
            if (_failedBatchId == request.Batch.BatchId && _failedAttempts.Add(request.Batch.AttemptId))
            {
                // Retry retains the batch identity and explicitly installs a new attempt.
                // A final probe for the failed attempt must never unlock a partial mutation.
                await _runtime.RecoverConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            NetworkSettingsConfiguration observed = await _runtime.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return _binding.Observe(request, key => Read(key, observed));
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            // A new edit can replace the failed batch. Its first failed probe records
            // identity only; the user's later explicit retry may recover the old runtime.
            RecordFailedAttempt(request);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        Validate(request, admissionLease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Validate(request, admissionLease);
            NetworkSettingsConfiguration before = await _runtime.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
            T Target<T>(SettingKey key) where T : notnull => request.Values.TryGetValue(key, out SettingValue? value)
                ? value.Get<T>() : (T)Read(key, before);
            NetworkSettingsConfiguration target = new(Target<ClashSharpMode>(SettingsRegistry.Keys.CurrentMode),
                Target<string>(SettingsRegistry.Keys.ActiveProfileId), Target<bool>(SettingsRegistry.Keys.TransparentProxyEnabled),
                Target<int>(SettingsRegistry.Keys.MixedPort));
            cancellationToken.ThrowIfCancellationRequested();
            // Once the complete native transaction starts, its rollback and verification belong
            // to this owner. Neither page cancellation nor generation retirement can detach it.
            try { await _runtime.ApplyConfigurationAsync(target, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                RecordFailedAttempt(request);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate) { return new(_retirement ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Volatile.Write(ref _retiring, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private void RecordFailedAttempt(SettingsApplicationRequest request)
    {
        if (_failedBatchId != request.Batch.BatchId) { _failedAttempts.Clear(); }
        _failedBatchId = request.Batch.BatchId;
        _failedAttempts.Add(request.Batch.AttemptId);
    }

    private void Validate(SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        _binding.Validate(request, lease);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _retiring) != 0, this);
        if (request.Phase == SettingsApplicationPhase.Startup) { _admission.EnsureActiveExclusiveLease(lease); }
        foreach ((SettingKey key, SettingValue value) in request.Values)
        {
            if (!value.Equals(SettingsRegistry.Default.Get(key.Value).Normalize(value.CanonicalText).Value))
            {
                throw new InvalidOperationException("The network attempt contains a noncanonical setting.");
            }
        }
    }

    private static object Read(SettingKey key, NetworkSettingsConfiguration configuration) => key == SettingsRegistry.Keys.CurrentMode
        ? configuration.Mode : key == SettingsRegistry.Keys.ActiveProfileId ? configuration.ProfileId
        : key == SettingsRegistry.Keys.TransparentProxyEnabled ? configuration.TransparentProxyEnabled : configuration.MixedPort;
}
