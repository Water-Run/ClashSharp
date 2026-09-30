using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Data;

/// <summary>Publishes a complete first generation or opens the durable winner before exposing any repository consumer.</summary>
public sealed class DataGenerationBootstrapper
{
    private readonly IDataGenerationStore _store;
    private readonly IDataGenerationBootstrapFactory _factory;
    private readonly DataGenerationManager _manager;
    private readonly MutationAdmissionBarrier _admission;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    /// <summary>Creates the startup coordinator without opening storage or constructing repositories.</summary>
    /// <param name="store">Durable manifest authority.</param>
    /// <param name="factory">Complete generation-local repository composition.</param>
    /// <param name="manager">Process-wide facade to initialize only after verification.</param>
    /// <param name="admission">The same barrier used by settings, import, and shutdown.</param>
    public DataGenerationBootstrapper(
        IDataGenerationStore store, IDataGenerationBootstrapFactory factory,
        DataGenerationManager manager, MutationAdmissionBarrier admission)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    /// <summary>Opens once under exclusive startup ownership; an uncertain publication is reread before choosing a scope.</summary>
    /// <remarks>Cancellation cannot abandon manifest outcome resolution after a promotion was attempted.</remarks>
    /// <param name="admissionLease">Caller-owned exclusive startup lease covering this operation and later reconciliation.</param>
    /// <param name="cancellationToken">Cancels work before publication begins.</param>
    public async Task<DataGenerationManifestSnapshot> InitializeAdmittedAsync(
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DataGenerationScope? ownedScope = null;
        try
        {
            _admission.EnsureActiveExclusiveLease(admissionLease);
            if (_initialized) { return _manager.CurrentManifest; }
            DataGenerationManifestSnapshot? manifest = await _store.LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                ownedScope = await _factory.CreateInitialAsync(admissionLease, cancellationToken).ConfigureAwait(false);
                if (ownedScope.State != DataGenerationScopeState.Staged || ownedScope.Descriptor.GenerationNumber != 1)
                {
                    throw new InvalidOperationException("First publication requires a complete paused generation numbered one.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                _admission.EnsureActiveExclusiveLease(admissionLease);
                manifest = await PublishOrReadWinnerAsync(ownedScope.Descriptor, cancellationToken).ConfigureAwait(false);
                if (!ownedScope.Descriptor.IsSameGeneration(manifest.Descriptor))
                {
                    await ownedScope.DisposeAsync().ConfigureAwait(false);
                    ownedScope = null;
                }

                // Once publication has been attempted, finish opening its durable winner even
                // if the caller canceled. The retained admission still owns the whole startup.
                cancellationToken = CancellationToken.None;
            }

            ownedScope ??= await _factory.OpenAsync(manifest.Descriptor, admissionLease, cancellationToken).ConfigureAwait(false);
            _admission.EnsureActiveExclusiveLease(admissionLease);
            _manager.Initialize(manifest, ownedScope);
            ownedScope = null; // Manager now owns retirement and disposal.
            _initialized = true;
            return manifest;
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            if (ownedScope is not null)
            {
                try { await ownedScope.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (!ExceptionGraphClassifier.IsProcessFatal(cleanup))
                {
                    throw new AggregateException("Generation startup and disposal both failed; durable data was retained.", failure, cleanup);
                }
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DataGenerationManifestSnapshot> PublishOrReadWinnerAsync(
        DataGenerationDescriptor candidate, CancellationToken cancellationToken)
    {
        Exception? publicationFailure = null;
        try
        {
            return await _store.PromoteAsync(candidate, expectedCurrentHash: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            publicationFailure = exception;
        }

        DataGenerationManifestSnapshot? winner;
        try
        {
            winner = await _store.LoadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception observationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(observationFailure))
        {
            throw new AggregateException(
                "The first generation publication could not be resolved; no repository facade was activated.",
                publicationFailure, observationFailure);
        }
        if (winner is not null) { return winner; }
        ExceptionDispatchInfo.Capture(publicationFailure).Throw();
        throw new InvalidOperationException("Unreachable publication outcome.");
    }
}
