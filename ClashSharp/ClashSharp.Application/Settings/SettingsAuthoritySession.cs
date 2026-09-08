using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Serializes asynchronous edits and verified application within one pinned repository lifetime.</summary>
/// <remarks>The generation owner keeps this session and its repository pinned for each complete call, including participant recovery.</remarks>
public sealed partial class SettingsAuthoritySession : IAsyncDisposable
{
    private readonly ISettingsRepository _repository;
    private readonly SettingsRegistry _registry;
    private readonly MutationAdmissionBarrier _admission;
    private readonly SettingsEnvelopeEditor _editor;
    private readonly SettingsApplicationBatchEditor _batches;
    private readonly SettingsEnvelopeValidator _validator;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private SettingsEnvelope? _snapshot;

    /// <summary>Creates one session without opening storage, starting tasks, or publishing fallback preferences.</summary>
    /// <param name="repository">Repository owned by the immutable generation lifetime.</param>
    /// <param name="registry">Canonical settings definitions.</param>
    /// <param name="admission">Process-wide mutation admission shared with import and shutdown.</param>
    /// <param name="timeProvider">Optional clock for completed observation timestamps.</param>
    public SettingsAuthoritySession(
        ISettingsRepository repository, SettingsRegistry registry, MutationAdmissionBarrier admission,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _editor = new(registry);
        _batches = new(registry);
        _validator = new(registry);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the immutable storage owner of this session.</summary>
    public DataGenerationDescriptor Generation => _repository.Generation;

    /// <summary>Gets the last verified immutable envelope; uncertain commits invalidate this projection until storage is reread.</summary>
    public SettingsEnvelope Snapshot
    {
        get
        {
            ThrowIfClosing();
            return Volatile.Read(ref _snapshot)
                ?? throw new InvalidOperationException("Settings authority has not been verified for this generation.");
        }
    }

    /// <summary>Opens an already initialized authority; migration remains owned by the exclusive startup bootstrapper.</summary>
    /// <param name="admissionLease">Active caller-owned lease.</param>
    /// <param name="cancellationToken">Cancels queued or read-only work.</param>
    public Task<SettingsAuthorityResult> OpenAdmittedAsync(MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
        ExecuteAdmittedAsync(admissionLease, ReadCoreAsync, cancellationToken);

    /// <summary>Replaces previous-process evidence with pending observation while preserving blocked probes and existing attempt identities.</summary>
    /// <param name="startupId">Fresh identity of the exclusively owned startup.</param>
    /// <param name="admissionLease">Exclusive startup lease held until reconciliation finishes.</param>
    /// <param name="cancellationToken">Cancels before the repository's atomic publication point.</param>
    public Task<SettingsAuthorityResult> PrepareStartupAdmittedAsync(
        Guid startupId, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        return EditAdmittedAsync(envelope => _batches.ScheduleStartupReconciliation(envelope, startupId), admissionLease, cancellationToken);
    }

    /// <summary>Commits a complete canonical change set using the current durable revision.</summary>
    /// <param name="changes">Desired changes, copied before asynchronous admission waiting.</param>
    /// <param name="transactionId">Stable identity for newly planned application batches.</param>
    /// <param name="admissionLease">Active caller-owned lease.</param>
    /// <param name="cancellationToken">Cancels only before the repository's atomic publication point.</param>
    public Task<SettingsAuthorityResult> ChangeAdmittedAsync(
        IEnumerable<SettingValueChange> changes, Guid transactionId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        return EditAdmittedAsync(envelope => _editor.ApplyChanges(envelope, snapshot, transactionId), admissionLease, cancellationToken);
    }

    /// <summary>Reverts selected desired preferences to verified evidence or the registry's explicit fallback while retaining necessary application work.</summary>
    /// <param name="keys">Canonical keys copied before asynchronous waiting.</param>
    /// <param name="transactionId">Stable edit identity.</param>
    /// <param name="admissionLease">Active caller-owned lease.</param>
    /// <param name="cancellationToken">Cancels before atomic publication.</param>
    public Task<SettingsAuthorityResult> RevertAdmittedAsync(
        IEnumerable<SettingKey> keys, Guid transactionId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SettingKey[] snapshot = keys.ToArray();
        return EditAdmittedAsync(envelope => _editor.Revert(envelope, snapshot, transactionId), admissionLease, cancellationToken);
    }

    /// <summary>Explicitly retries failed work under a new attempt identity.</summary>
    /// <param name="batchId">Exact batch.</param>
    /// <param name="expectedAttemptId">Failed attempt being replaced.</param>
    /// <param name="newAttemptId">New nonempty attempt identity.</param>
    /// <param name="admissionLease">Active caller-owned lease.</param>
    /// <param name="cancellationToken">Cancels before atomic publication.</param>
    public Task<SettingsAuthorityResult> RetryAdmittedAsync(
        Guid batchId, Guid expectedAttemptId, Guid newAttemptId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
        EditAdmittedAsync(envelope => _batches.RetryFailed(envelope, batchId, expectedAttemptId, newAttemptId), admissionLease, cancellationToken);

    private Task<SettingsAuthorityResult> EditAdmittedAsync(
        Func<SettingsEnvelope, SettingsEnvelopeEditResult> edit, MutationAdmissionLease lease, CancellationToken token) =>
        ExecuteAdmittedAsync(lease, async waiting =>
        {
            SettingsAuthorityResult read = await ReadCoreAsync(waiting).ConfigureAwait(false);
            return !read.IsSucceeded ? read
                : await PersistEditAsync(read.Envelope!, edit(read.Envelope!), waiting).ConfigureAwait(false);
        }, token);

    private async Task<SettingsAuthorityResult> ExecuteAdmittedAsync(
        MutationAdmissionLease lease, Func<CancellationToken, Task<SettingsAuthorityResult>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfClosing();
        _admission.EnsureActiveLease(lease);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RevocationToken);
        await _operationGate.WaitAsync(waiting.Token).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            _admission.EnsureActiveLease(lease);
            waiting.Token.ThrowIfCancellationRequested();
            return await operation(waiting.Token).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<SettingsAuthorityResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            SettingsPersistenceResult result = await _repository.OpenAsync(cancellationToken).ConfigureAwait(false);
            return ObservePersistence(result);
        }
        catch
        {
            Volatile.Write(ref _snapshot, null);
            throw;
        }
    }

    private async Task<SettingsAuthorityResult> PersistEditAsync(
        SettingsEnvelope source, SettingsEnvelopeEditResult edit, CancellationToken cancellationToken)
    {
        if (!edit.IsSuccess) { return new(SettingsAuthorityStatus.Rejected, source, edit.ErrorCode); }
        if (edit.Outcome == SettingsEnvelopeEditOutcome.NoChange) { return new(SettingsAuthorityStatus.NoChange, source); }
        try
        {
            SettingsPersistenceResult persisted = await _repository.SaveAsync(edit.Envelope, source.EnvelopeRevision, cancellationToken)
                .ConfigureAwait(false);
            return ObservePersistence(persisted);
        }
        catch
        {
            Volatile.Write(ref _snapshot, null);
            throw;
        }
    }

    private SettingsAuthorityResult ObservePersistence(SettingsPersistenceResult result)
    {
        if (result.IsSucceeded && result.Envelope is null)
        {
            result = SettingsPersistenceResult.Invalid(new("settings.authority.uninitialized", "envelope"));
        }

        SettingsEnvelope? observed = result.Status is SettingsPersistenceStatus.Succeeded or SettingsPersistenceStatus.Conflict
            ? result.Envelope : null;
        if (observed is not null && !_validator.Validate(observed).IsValid)
        {
            observed = null;
            result = SettingsPersistenceResult.Invalid(new("settings.authority.invalid_observation", "envelope"));
        }

        Volatile.Write(ref _snapshot, observed);
        if (result.IsSucceeded && observed is not null) { return new(SettingsAuthorityStatus.Succeeded, observed); }
        return new(SettingsAuthorityStatus.PersistenceFailed, observed,
            result.Diagnostic?.Code ?? (result.Status == SettingsPersistenceStatus.Conflict
                ? "settings.authority.conflict" : "settings.authority.uninitialized"), result.Status);
    }
}
