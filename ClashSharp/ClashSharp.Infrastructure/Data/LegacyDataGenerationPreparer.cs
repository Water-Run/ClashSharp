using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Data;

/// <summary>Copies legacy user repositories and canonical preferences into an unpublished first generation.</summary>
/// <remarks>Call only after retained legacy transactions have recovered and before runtime producers start.</remarks>
public sealed class LegacyDataGenerationPreparer
{
    private readonly string _legacyRoot;
    private readonly DataGenerationPathPolicy _paths;
    private readonly MutationAdmissionBarrier _admission;
    private readonly ILegacySettingsSource _legacySettings;
    private readonly SettingsRegistry _registry;

    /// <summary>Creates a preparer without inspecting legacy storage or allocating a generation.</summary>
    /// <param name="applicationDataRoot">Canonical application root containing the legacy repositories.</param>
    /// <param name="admission">Process-wide ownership barrier shared with bootstrap.</param>
    /// <param name="legacySettings">Read-only legacy preference source; credentials are excluded.</param>
    /// <param name="registry">Canonical settings definitions.</param>
    public LegacyDataGenerationPreparer(
        string applicationDataRoot, MutationAdmissionBarrier admission,
        ILegacySettingsSource legacySettings, SettingsRegistry registry)
    {
        _paths = new(applicationDataRoot);
        _legacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDataRoot));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _legacySettings = legacySettings ?? throw new ArgumentNullException(nameof(legacySettings));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Creates a complete data copy without replacing legacy files or publishing a current manifest.</summary>
    /// <remarks>Failed candidates are retained for diagnosis; a retry always allocates a different immutable root.</remarks>
    /// <param name="admissionLease">Exclusive startup ownership covering retained transaction recovery and migration.</param>
    /// <param name="cancellationToken">Cancels copying or initialization before publication.</param>
    /// <returns>The candidate to verify with the actual repository container before manifest promotion.</returns>
    public async Task<DataGenerationDescriptor> PrepareAdmittedAsync(
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        if (await new FileDataGenerationStore(_legacyRoot).LoadCurrentAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("Legacy migration cannot replace an existing data generation.");
        }

        DataGenerationDescriptor candidate = _paths.CreateGeneration(Guid.NewGuid(), 1);
        await new UserDataRepositorySnapshot(_legacyRoot, _legacyRoot, _admission)
            .CopyAdmittedAsync(candidate, admissionLease, requireExisting: false, cancellationToken).ConfigureAwait(false);

        _admission.EnsureActiveExclusiveLease(admissionLease);
        JsonSettingsRepository repository = new(candidate, _registry);
        SettingsAuthorityBootstrapper bootstrapper = new(repository, _legacySettings, new(_registry));
        SettingsPersistenceResult preferences = await bootstrapper.OpenAsync(candidate.GenerationId, cancellationToken).ConfigureAwait(false);
        if (!preferences.IsSucceeded || preferences.Envelope is null)
        {
            throw new InvalidDataException($"Initial settings could not be verified: {preferences.Status} ({preferences.Diagnostic?.Code}).");
        }
        _paths.ValidateDescriptor(candidate);
        return candidate;
    }
}
