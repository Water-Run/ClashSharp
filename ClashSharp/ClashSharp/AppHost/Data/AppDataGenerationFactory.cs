using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Builds and verifies a complete paused production repository container before bootstrap can publish or activate it.</summary>
internal sealed class AppDataGenerationFactory : IDataGenerationBootstrapFactory
{
    private readonly DataGenerationPathPolicy _paths;
    private readonly LegacyDataGenerationPreparer _preparer;
    private readonly MutationAdmissionBarrier _admission;
    private readonly Func<MutationAdmissionLease, CancellationToken, Task> _recoverLegacy;
    private readonly Func<SettingsAuthoritySession, AppDataGenerationRepositories> _createRepositories;
    private readonly IReadOnlyDictionary<SettingApplicationKind, Func<AppDataGenerationRepositories, ISettingsApplicationParticipant>> _participants;

    public AppDataGenerationFactory(
        string applicationDataRoot, MutationAdmissionBarrier admission, ILegacySettingsSource legacySettings,
        Func<MutationAdmissionLease, CancellationToken, Task> recoverLegacy,
        Func<SettingsAuthoritySession, AppDataGenerationRepositories> createRepositories,
        IReadOnlyDictionary<SettingApplicationKind, Func<AppDataGenerationRepositories, ISettingsApplicationParticipant>> participants)
    {
        _paths = new(applicationDataRoot);
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _preparer = new(applicationDataRoot, admission, legacySettings, SettingsRegistry.Default);
        _recoverLegacy = recoverLegacy ?? throw new ArgumentNullException(nameof(recoverLegacy));
        _createRepositories = createRepositories ?? throw new ArgumentNullException(nameof(createRepositories));
        ArgumentNullException.ThrowIfNull(participants);
        _participants = participants.ToDictionary(pair => pair.Key, pair => pair.Value);
        SettingApplicationKind[] expected = SettingsRegistry.Default.Definitions.Select(definition => definition.ApplicationKind).Distinct().Order().ToArray();
        if (!expected.SequenceEqual(_participants.Keys.Order()) || _participants.Values.Any(factory => factory is null))
        {
            throw new ArgumentException("Every settings application kind requires one generation-local factory.", nameof(participants));
        }
    }

    public async Task<DataGenerationScope> CreateInitialAsync(MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        await _recoverLegacy(admissionLease, cancellationToken).ConfigureAwait(false);
        DataGenerationDescriptor descriptor = await _preparer.PrepareAdmittedAsync(admissionLease, cancellationToken).ConfigureAwait(false);
        return await OpenCoreAsync(descriptor, allowCreate: true, admissionLease, cancellationToken).ConfigureAwait(false);
    }

    public Task<DataGenerationScope> OpenAsync(
        DataGenerationDescriptor descriptor, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
        OpenCoreAsync(descriptor, allowCreate: false, admissionLease, cancellationToken);

    private async Task<DataGenerationScope> OpenCoreAsync(
        DataGenerationDescriptor descriptor, bool allowCreate, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        cancellationToken.ThrowIfCancellationRequested();
        _paths.ValidateDescriptor(descriptor);
        SettingsAuthoritySession session = new(new JsonSettingsRepository(descriptor, SettingsRegistry.Default), SettingsRegistry.Default, _admission);
        AppDataGenerationRepositories? repositories = null;
        try
        {
            repositories = _createRepositories(session);
            if (!ReferenceEquals(repositories.Session, session)) { throw new InvalidOperationException("Repository composition replaced its owning settings session."); }
            await repositories.OpenStorageAsync(allowCreate, admissionLease, cancellationToken).ConfigureAwait(false);
            foreach ((SettingApplicationKind kind, Func<AppDataGenerationRepositories, ISettingsApplicationParticipant> create) in _participants.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ISettingsApplicationParticipant participant = create(repositories)
                    ?? throw new InvalidOperationException("A generation settings factory returned no participant.");
                if (participant.ApplicationKind != kind)
                {
                    if (participant is IAsyncDisposable asynchronous) { await asynchronous.DisposeAsync().ConfigureAwait(false); }
                    else if (participant is IDisposable synchronous) { synchronous.Dispose(); }
                    throw new InvalidOperationException("A generation settings factory returned the wrong application kind.");
                }
                repositories.OwnSettingsParticipant(participant);
            }
            repositories.SealComposition();
            return new(descriptor, repositories);
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            try
            {
                if (repositories is not null) { await repositories.DisposeAsync().ConfigureAwait(false); }
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) when (!ExceptionGraphClassifier.IsProcessFatal(cleanup))
            {
                throw new AggregateException("Opening the generation and retiring its repositories both failed.", failure, cleanup);
            }
            throw;
        }
    }
}
