using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Builds complete unpublished import and full-settings-reset candidates over a drained source generation.</summary>
/// <remarks>The transition owner must quiesce producers first and retain exclusive admission through the final runtime decision.</remarks>
internal sealed class GenerationDataCandidatePreparer(
    string applicationDataRoot, MutationAdmissionBarrier admission, IDataGenerationBootstrapFactory factory)
{
    private readonly DataGenerationPathPolicy _paths = new(applicationDataRoot);

    public Task<DataGenerationScope> PrepareImportAdmittedAsync(DataGenerationTransition transition, string packagePath,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        return Task.Run(() => PrepareAsync(transition, admissionLease, (baseline, source, token) =>
            new ClashDataPackageService(new DataPackageSettingsSnapshot(baseline), source.RootPath)
                .ReadImportPlan(packagePath, token), cancellationToken), cancellationToken);
    }

    public Task<DataGenerationScope> PrepareResetAdmittedAsync(DataGenerationTransition transition,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        return Task.Run(() => PrepareAsync(transition, admissionLease, (_, _, _) => new DataPackageImportPlan(
            Model.ClashDataPackageScope.Settings,
            SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.All)
                .Select(definition => new SettingValueChange(definition.Key, definition.DefaultValue)).ToArray(), []), cancellationToken), cancellationToken);
    }

    private async Task<DataGenerationScope> PrepareAsync(DataGenerationTransition transition, MutationAdmissionLease admissionLease,
        Func<SettingsEnvelope, DataGenerationDescriptor, CancellationToken, DataPackageImportPlan> readPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        if (transition.BaselineScopeState != DataGenerationScopeState.Draining
            || transition.IsManifestPromoted || transition.StagedDescriptor is not null)
        {
            throw new InvalidOperationException("Candidate preparation requires an unpromoted, drained transition.");
        }
        DataGenerationManifestSnapshot expected = transition.BaselineManifest;
        DataGenerationManifestSnapshot? current = await new FileDataGenerationStore(applicationDataRoot)
            .LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current?.ContentHash != expected.ContentHash) { throw new InvalidOperationException("The source generation changed before preparation."); }
        _paths.ValidateDescriptor(expected.Descriptor);
        SettingsPersistenceResult source = await new JsonSettingsRepository(expected.Descriptor, SettingsRegistry.Default)
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!source.IsSucceeded || source.Envelope is null) { throw new InvalidDataException("The source settings could not be verified."); }

        // Decode all package entries and validate settings before allocating any candidate.
        DataPackageImportPlan plan = readPlan(source.Envelope, expected.Descriptor, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Guid identity = Guid.NewGuid();
        SettingsEnvelope desired = new SettingsGenerationPlanner(SettingsRegistry.Default).Create(source.Envelope, plan.Settings, identity);
        DataGenerationDescriptor candidate = _paths.CreateGeneration(identity, checked(expected.HighestGenerationNumber + 1));
        await new UserDataRepositorySnapshot(applicationDataRoot, expected.Descriptor.RootPath, admission)
            .CopyAdmittedAsync(candidate, admissionLease, requireExisting: true, cancellationToken).ConfigureAwait(false);
        foreach (DataPackageImportFile file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.GetFullPath(Path.Combine(candidate.RootPath, file.RelativePath));
            if (!DataGenerationPathPolicy.IsContainedBy(candidate.RootPath, destination)) { throw new InvalidDataException("An imported file escaped the candidate."); }
            _paths.ValidateStagingPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            _paths.ValidateStagingPath(destination);
            await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await output.WriteAsync(file.Content, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        SettingsPersistenceResult persisted = await new JsonSettingsRepository(candidate, SettingsRegistry.Default)
            .SaveAsync(desired, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
        if (!persisted.IsSucceeded || persisted.Envelope is null) { throw new InvalidDataException("The candidate settings could not be verified."); }
        admission.EnsureActiveExclusiveLease(admissionLease);
        _paths.ValidateDescriptor(candidate);
        // The production factory opens actual catalogs and databases and owns cleanup if validation fails.
        return await factory.OpenAsync(candidate, admissionLease, cancellationToken).ConfigureAwait(false);
    }
}
