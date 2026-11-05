using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Service;

/// <summary>Admits complete profile-catalog mutations through the process-wide mutation boundary.</summary>
internal interface IProfileCatalogMutationCoordinator
{
    Task<T> ExecuteAsync<T>(
        Guid operationId,
        Func<MutationAdmissionLease?, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken);

    /// <summary>Retains admission during preparation and enters shared ordering only for the final mutation.</summary>
    Task<TResult> ExecutePreparedAsync<TPrepared, TResult>(
        Guid operationId, Func<CancellationToken, Task<TPrepared>> prepare,
        Func<TPrepared, MutationAdmissionLease?, CancellationToken, Task<TResult>> mutation,
        CancellationToken cancellationToken, Action<Exception>? onFailure = null) =>
        ExecuteAsync(operationId, async (lease, token) =>
        {
            try
            {
                TPrepared prepared = await prepare(token).ConfigureAwait(false);
                return await mutation(prepared, lease, token).ConfigureAwait(false);
            }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                onFailure?.Invoke(failure);
                throw;
            }
        }, cancellationToken);
}

/// <summary>Shares network mutation admission and fair ordering without introducing another journal.</summary>
internal sealed class ProfileCatalogMutationCoordinator(
    MutationAdmissionBarrier admissionBarrier,
    FairAsyncMutationGate mutationGate) : IProfileCatalogMutationCoordinator
{
    private readonly MutationAdmissionBarrier _admissionBarrier =
        admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));

    private readonly FairAsyncMutationGate _mutationGate =
        mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));

    public Task<T> ExecuteAsync<T>(
        Guid operationId,
        Func<MutationAdmissionLease?, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return ExecutePreparedAsync(operationId, _ => Task.FromResult(true),
            (_, lease, token) => mutation(lease, token), cancellationToken);
    }

    public async Task<TResult> ExecutePreparedAsync<TPrepared, TResult>(
        Guid operationId, Func<CancellationToken, Task<TPrepared>> prepare,
        Func<TPrepared, MutationAdmissionLease?, CancellationToken, Task<TResult>> mutation,
        CancellationToken cancellationToken, Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(mutation);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A profile mutation operation identifier cannot be empty.", nameof(operationId));
        }

        MutationAdmissionLease admissionLease = await _admissionBarrier
            .AcquireOrdinaryAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using CancellationTokenSource admittedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    admissionLease.RevocationToken);
            try
            {
                TPrepared prepared = await prepare(admittedCancellation.Token).ConfigureAwait(false);
                return await _mutationGate
                    .ExecuteAsync(
                        operationId,
                        (_, gateToken) => mutation(prepared, admissionLease, gateToken),
                        admittedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                admissionLease.RevocationToken.IsCancellationRequested && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                // The scheduler's own stop token can still be live while shutdown drains admission.
                // Preserve the trusted cancellation cause without disguising independent HTTP timeouts.
                ProfileCatalogMutationCanceledException cancelled = new(exception, admittedCancellation.Token);
                onFailure?.Invoke(cancelled);
                throw cancelled;
            }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                onFailure?.Invoke(failure);
                throw;
            }
        }
        finally
        {
            await admissionLease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Late-bound production bridge needed by the legacy static catalog singleton. Mutations fail closed
/// until the application host supplies the process-wide admission and fair-gate instances.
/// </summary>
internal sealed class LateBoundProfileCatalogMutationCoordinator : IProfileCatalogMutationCoordinator
{
    private readonly object _syncLock = new();
    private IProfileCatalogMutationCoordinator? _configured;

    internal static LateBoundProfileCatalogMutationCoordinator Instance { get; } = new();

    private LateBoundProfileCatalogMutationCoordinator()
    {
    }

    internal void Configure(
        MutationAdmissionBarrier admissionBarrier,
        FairAsyncMutationGate mutationGate)
    {
        lock (_syncLock)
        {
            _configured = new ProfileCatalogMutationCoordinator(admissionBarrier, mutationGate);
        }
    }

    public Task<T> ExecuteAsync<T>(
        Guid operationId,
        Func<MutationAdmissionLease?, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        IProfileCatalogMutationCoordinator coordinator;
        lock (_syncLock)
        {
            coordinator = _configured
                ?? throw new InvalidOperationException(
                    "Profile catalog mutations are unavailable before process-wide mutation admission is configured.");
        }

        return coordinator.ExecuteAsync(operationId, mutation, cancellationToken);
    }

    public Task<TResult> ExecutePreparedAsync<TPrepared, TResult>(
        Guid operationId, Func<CancellationToken, Task<TPrepared>> prepare,
        Func<TPrepared, MutationAdmissionLease?, CancellationToken, Task<TResult>> mutation,
        CancellationToken cancellationToken, Action<Exception>? onFailure = null)
    {
        IProfileCatalogMutationCoordinator coordinator;
        lock (_syncLock)
        {
            coordinator = _configured ?? throw new InvalidOperationException(
                "Profile catalog mutations are unavailable before process-wide mutation admission is configured.");
        }
        return coordinator.ExecutePreparedAsync(operationId, prepare, mutation, cancellationToken, onFailure);
    }
}

/// <summary>Explicit coordinator for isolated unit tests that do not construct an application host.</summary>
internal sealed class UncoordinatedProfileCatalogMutationCoordinator : IProfileCatalogMutationCoordinator
{
    internal static UncoordinatedProfileCatalogMutationCoordinator Instance { get; } = new();

    private UncoordinatedProfileCatalogMutationCoordinator()
    {
    }

    public Task<T> ExecuteAsync<T>(
        Guid operationId,
        Func<MutationAdmissionLease?, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A profile mutation operation identifier cannot be empty.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        return mutation(null, cancellationToken);
    }
}
