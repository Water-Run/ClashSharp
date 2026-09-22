using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Opens a pinned settings authority or initializes a demonstrably unused repository from legacy preferences.</summary>
/// <remarks>The caller retains exclusive startup or generation-transition ownership for the complete operation.</remarks>
public sealed class SettingsAuthorityBootstrapper
{
    private readonly ISettingsRepository _repository;
    private readonly ILegacySettingsSource _legacy;
    private readonly SettingsMigrationPlanner _migration;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    /// <summary>Creates the initializer without reading either storage system.</summary>
    /// <param name="repository">Repository pinned to the generation being opened.</param>
    /// <param name="legacy">Read-only source consulted only for an unused repository.</param>
    /// <param name="migration">Canonical legacy normalization and initial pending-state planner.</param>
    public SettingsAuthorityBootstrapper(
        ISettingsRepository repository,
        ILegacySettingsSource legacy,
        SettingsMigrationPlanner migration)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _migration = migration ?? throw new ArgumentNullException(nameof(migration));
    }

    /// <summary>Returns a verified nonempty authority or a persistence failure without silently replacing existing data.</summary>
    /// <param name="migrationId">Stable identity for a possible first migration.</param>
    /// <param name="cancellationToken">Cancels observation and repository work before its durable commit point.</param>
    public async Task<SettingsPersistenceResult> OpenAsync(Guid migrationId, CancellationToken cancellationToken)
    {
        if (migrationId == Guid.Empty)
        {
            throw new ArgumentException("Migration identity cannot be empty.", nameof(migrationId));
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SettingsPersistenceResult existing = await _repository.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!existing.IsSucceeded || existing.Envelope is not null)
            {
                return existing;
            }

            cancellationToken.ThrowIfCancellationRequested();
            LegacySettingsSnapshot snapshot = await _legacy.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SettingsMigrationPlan plan = _migration.CreatePlan(snapshot, migrationId);
            SettingsPersistenceResult saved = await _repository.SaveAsync(
                plan.Envelope, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
            if (saved.Status == SettingsPersistenceStatus.Conflict && saved.Envelope is not null)
            {
                // Another initializer won the atomic first publication. Its verified
                // authority wins; never repeat migration over the observed revision.
                return SettingsPersistenceResult.Succeeded(saved.Envelope);
            }

            if (saved.IsSucceeded && saved.Envelope is null)
            {
                return SettingsPersistenceResult.Invalid(new SettingsPersistenceDiagnostic(
                    "settings.bootstrap.empty_commit", "envelope"));
            }

            return saved;
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
