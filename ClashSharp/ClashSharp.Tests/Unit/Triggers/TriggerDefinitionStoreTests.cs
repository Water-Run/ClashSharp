using System.Collections;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Tests.Unit.Triggers;

/// <summary>Verifies the presentation facade retains only successfully observed repository generations.</summary>
public sealed class TriggerDefinitionStoreTests
{
    [Fact]
    public async Task ReadAsync_ProjectsAndCachesTheAuthoritativeOrderedDefinitions()
    {
        using TemporaryDirectory directory = new();
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
        TriggerTaskDefinition first = Definition("first", "First");
        TriggerTaskDefinition second = Definition("second", "Second");
        Assert.True((await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [first, second]),
            CancellationToken.None)).IsSucceeded);
        TriggerDefinitionStore store = new(repository, new FixedTimeProvider());

        TriggerPersistenceResult<TriggerDefinitionCatalog> result = await store.ReadAsync(
            CancellationToken.None);

        TriggerDefinitionCatalog catalog = Assert.IsType<TriggerDefinitionCatalog>(result.Value);
        Assert.True(result.IsSucceeded);
        Assert.Same(catalog, store.Current);
        Assert.Equal(1, catalog.Generation);
        Assert.Equal(["first", "second"], catalog.Tasks.Select(task => task.Definition.Id));
    }

    [Fact]
    public async Task ReplaceAsync_ConflictDoesNotPublishAnUncommittedCacheProjection()
    {
        using TemporaryDirectory directory = new();
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
        TriggerDefinitionStore store = new(repository, new FixedTimeProvider());
        Assert.True((await store.ReadAsync(CancellationToken.None)).IsSucceeded);
        TriggerTaskDefinition committedElsewhere = Definition("external", "External");
        Assert.True((await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [committedElsewhere]),
            CancellationToken.None)).IsSucceeded);

        TriggerPersistenceResult<TriggerDefinitionCatalog> conflict = await store.ReplaceAsync(
            0,
            [Definition("local", "Local")],
            CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Conflict, conflict.Status);
        Assert.Equal(0, store.Current.Generation);
        Assert.Empty(store.Current.Tasks);
        TriggerDefinitionCatalog refreshed = Assert.IsType<TriggerDefinitionCatalog>(
            (await store.ReadAsync(CancellationToken.None)).Value);
        Assert.Equal("external", Assert.Single(refreshed.Tasks).Definition.Id);
    }

    [Fact]
    public async Task ReplaceAsync_PublishesTheValidatedRequestSnapshotWithoutReenumeratingTheCaller()
    {
        using TemporaryDirectory directory = new();
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
        TriggerDefinitionStore store = new(repository, new FixedTimeProvider());
        Assert.True((await store.ReadAsync(CancellationToken.None)).IsSucceeded);
        SingleEnumerationReadOnlyList<TriggerTaskDefinition> definitions =
            new(Definition("local", "Local"));

        TriggerPersistenceResult<TriggerDefinitionCatalog> replaced = await store.ReplaceAsync(
            0,
            definitions,
            CancellationToken.None);

        Assert.True(replaced.IsSucceeded);
        Assert.Equal("local", Assert.Single(replaced.Value!.Tasks).Definition.Id);
    }

    [Fact]
    public async Task PreCanceledOperations_PropagateWithoutPublishingAProjectedCache()
    {
        using TemporaryDirectory directory = new();
        SqliteTriggerRepository repository = new(directory.DatabasePath);
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
        TriggerDefinitionStore store = new(repository, new FixedTimeProvider());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReplaceAsync(
                0,
                [Definition("local", "Local")],
                cancellation.Token));

        Assert.Same(TriggerDefinitionCatalog.Empty, store.Current);
    }

    private static TriggerTaskDefinition Definition(string id, string name)
    {
        return new TriggerTaskDefinition(
            id,
            1,
            name,
            true,
            [
                new TriggerCondition(
                    $"{id}-condition",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters(name)),
            ]);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class SingleEnumerationReadOnlyList<T>(params T[] items) : IReadOnlyList<T>
    {
        private int _enumerationCount;

        public int Count => items.Length;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
            {
                throw new InvalidOperationException("The caller collection was enumerated more than once.");
            }

            return ((IEnumerable<T>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Tests",
            $"trigger-definition-store-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_root, "Triggers.db");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
