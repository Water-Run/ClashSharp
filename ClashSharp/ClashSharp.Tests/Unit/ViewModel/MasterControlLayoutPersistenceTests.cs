using System.Collections.ObjectModel;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Fact]
    public async Task InfoLayout_WaitsForStorageBeforeChangingSelection()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterInfoTileLayoutService store = new() { BeforeSaveAsync = _ => release.Task };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        string[] previous = viewModel.VisibleInfoTiles.Select(static tile => tile.Id).ToArray();

        Task save = viewModel.SetVisibleInfoTileIdsAsync(["latency"], CancellationToken.None);
        try
        {
            Assert.False(save.IsCompleted);
            Assert.True(viewModel.IsSavingTileLayout);
            Assert.False(viewModel.CanEditTileLayout);
            Assert.Equal(previous, viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        }
        finally { release.TrySetResult(); }
        await save;

        Assert.Equal(["latency"], viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        Assert.True(viewModel.CanEditTileLayout);
        Assert.False(viewModel.HasOperationError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoLayout_FailureReflectsActualStorageAndRevertsAnUncommittedDrag(bool committedBeforeFailure)
    {
        FakeMasterInfoTileLayoutService store = new() { SavedLayout = ["core", "latency", "memory-usage"] };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        ObservableCollection<MasterControlInfoTileViewModel> nativeItems =
            Assert.IsType<ObservableCollection<MasterControlInfoTileViewModel>>(viewModel.VisibleInfoTiles);
        nativeItems.Move(0, 2);
        IOException failure = new("Synthetic layout failure");
        if (committedBeforeFailure) { store.AfterSaveAsync = _ => Task.FromException(failure); }
        else { store.BeforeSaveAsync = _ => Task.FromException(failure); }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            viewModel.SetVisibleInfoTileIdsAsync(nativeItems.Select(static tile => tile.Id), CancellationToken.None)));

        string[] expected = committedBeforeFailure ? ["latency", "memory-usage", "core"] : ["core", "latency", "memory-usage"];
        Assert.Equal(expected, store.SavedLayout);
        Assert.Equal(expected, viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        Assert.True(viewModel.HasOperationError);
        Assert.True(viewModel.CanEditTileLayout);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task InfoLayout_CancellationBeforeCommitRestoresThePreviousOrder()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterInfoTileLayoutService store = new() { BeforeSaveAsync = token => release.Task.WaitAsync(token) };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        string[] previous = viewModel.VisibleInfoTiles.Select(static tile => tile.Id).ToArray();

        Task save = viewModel.SetVisibleInfoTileIdsAsync(["latency"], cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);

        Assert.Equal(previous, viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        Assert.Equal(previous, store.SavedLayout);
        Assert.False(viewModel.HasOperationError);
        Assert.True(viewModel.CanEditTileLayout);
    }

    [Fact]
    public async Task InfoLayout_CancellationAfterCommitStillPublishesTheSavedOrder()
    {
        using CancellationTokenSource cancellation = new();
        FakeMasterInfoTileLayoutService store = new()
        {
            AfterSaveAsync = _ => { cancellation.Cancel(); return Task.CompletedTask; },
        };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.SetVisibleInfoTileIdsAsync(["latency"], cancellation.Token);

        Assert.Equal(["latency"], store.SavedLayout);
        Assert.Equal(store.SavedLayout, viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        Assert.False(viewModel.HasOperationError);
    }

    [Fact]
    public async Task InfoLayout_QueuedInputCannotBeChangedByItsCaller()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterInfoTileLayoutService store = new() { BeforeSaveAsync = _ => release.Task };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        Task first = viewModel.SetVisibleInfoTileIdsAsync(["core"], CancellationToken.None);
        List<string> requested = ["latency"];
        Task second = viewModel.SetVisibleInfoTileIdsAsync(requested, CancellationToken.None);
        requested.Clear();
        requested.Add("memory-usage");
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["latency"], store.SavedLayout);
        Assert.Equal(store.SavedLayout, viewModel.VisibleInfoTiles.Select(static tile => tile.Id));
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task InfoLayout_QueueCancellationDoesNotWriteOrClearTheCurrentSaveState()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterInfoTileLayoutService store = new() { BeforeSaveAsync = _ => release.Task };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        Task first = viewModel.SetVisibleInfoTileIdsAsync(["core"], CancellationToken.None);
        Task second = viewModel.SetVisibleInfoTileIdsAsync(["latency"], cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.True(viewModel.IsSavingTileLayout);
            Assert.Equal(1, store.SaveCount);
        }
        finally { release.TrySetResult(); }
        await first;

        Assert.Equal(["core"], store.SavedLayout);
        Assert.True(viewModel.CanEditTileLayout);
    }

    [Fact]
    public async Task InfoLayout_NativeDragCanBeRestoredWhenThePageQueueNeverStartsItsWrite()
    {
        FakeMasterInfoTileLayoutService store = new() { SavedLayout = ["core", "latency", "memory-usage"] };
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        ObservableCollection<MasterControlInfoTileViewModel> nativeItems =
            Assert.IsType<ObservableCollection<MasterControlInfoTileViewModel>>(viewModel.VisibleInfoTiles);
        nativeItems.Move(0, 2);

        viewModel.RestoreCommittedInfoTileOrder();

        Assert.Equal(store.SavedLayout, nativeItems.Select(static tile => tile.Id));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task InfoLayout_ReconciliationFailurePreservesBothErrorsAndAllowsRetry()
    {
        FakeMasterInfoTileLayoutService store = new();
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        IOException writeFailure = new("Write failed");
        IOException readFailure = new("Read failed");
        store.BeforeSaveAsync = _ => Task.FromException(writeFailure);
        store.ReadFailure = readFailure;

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            viewModel.SetVisibleInfoTileIdsAsync(["latency"], CancellationToken.None));

        Assert.Equal([writeFailure, readFailure], error.InnerExceptions);
        Assert.True(viewModel.CanEditTileLayout);
        Assert.True(viewModel.HasOperationError);
        store.BeforeSaveAsync = null;
        store.ReadFailure = null;
        await viewModel.SetVisibleInfoTileIdsAsync([], CancellationToken.None);
        Assert.Empty(viewModel.VisibleInfoTiles);
        Assert.False(viewModel.HasOperationError);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that layout persistence does not hide nested fatal exceptions.")]
    public async Task InfoLayout_FatalFailureEscapesWithoutAttemptingRecovery()
    {
        FakeMasterInfoTileLayoutService store = new();
        MasterControlViewModel viewModel = CreateViewModel(infoTileLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        int reads = store.GetLayoutCount;
        IOException fatal = new("Synthetic failure", new OutOfMemoryException());
        store.BeforeSaveAsync = _ => Task.FromException(fatal);

        Assert.Same(fatal, await Assert.ThrowsAsync<IOException>(() =>
            viewModel.SetVisibleInfoTileIdsAsync(["latency"], CancellationToken.None)));

        Assert.Equal(reads, store.GetLayoutCount);
        Assert.True(viewModel.CanEditTileLayout);
    }

    [Fact]
    public async Task HeroLayout_QueuedEditsComposeAgainstTheLatestCommittedSlots()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterHeroStatusLayoutService store = new() { BeforeSaveAsync = _ => release.Task };
        MasterControlViewModel viewModel = CreateViewModel(heroStatusLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        Task first = viewModel.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.ActiveConnections, CancellationToken.None);
        Task second = viewModel.SetHeroStatusSlotAsync(1, MasterHeroStatusItemKind.Latency, CancellationToken.None);
        try
        {
            Assert.Equal(MasterHeroStatusItemKind.CoreStatus, viewModel.HeroStatusItems[0].Kind);
            Assert.False(viewModel.CanEditTileLayout);
            Assert.Equal(1, store.SaveCount);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);

        Assert.Equal(MasterHeroStatusItemKind.ActiveConnections, viewModel.HeroStatusItems[0].Kind);
        Assert.Equal(MasterHeroStatusItemKind.Latency, viewModel.HeroStatusItems[1].Kind);
        Assert.Equal(store.SavedLayout, viewModel.HeroStatusItems.Select(static item => item.Kind));
        Assert.Equal(2, store.SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeroLayout_FailureReconcilesSlotsWithoutASecondWrite(bool committedBeforeFailure)
    {
        FakeMasterHeroStatusLayoutService store = new();
        MasterControlViewModel viewModel = CreateViewModel(heroStatusLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        IOException failure = new("Synthetic hero layout failure");
        if (committedBeforeFailure) { store.AfterSaveAsync = _ => Task.FromException(failure); }
        else { store.BeforeSaveAsync = _ => Task.FromException(failure); }

        await Assert.ThrowsAsync<IOException>(() => viewModel.SetHeroStatusSlotAsync(
            0, MasterHeroStatusItemKind.ActiveConnections, CancellationToken.None));

        MasterHeroStatusItemKind expected = committedBeforeFailure ? MasterHeroStatusItemKind.ActiveConnections : MasterHeroStatusItemKind.CoreStatus;
        Assert.Equal(expected, viewModel.HeroStatusSlots[0].SelectedKind);
        Assert.Equal(expected, viewModel.HeroStatusItems[0].Kind);
        Assert.True(viewModel.HasOperationError);
        Assert.True(viewModel.CanEditTileLayout);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task HeroLayout_FailedResetPreservesTheSavedCustomLayout()
    {
        FakeMasterHeroStatusLayoutService store = new();
        MasterControlViewModel viewModel = CreateViewModel(heroStatusLayout: store);
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.ActiveConnections, CancellationToken.None);
        store.BeforeSaveAsync = _ => Task.FromException(new IOException("Reset failed"));

        await Assert.ThrowsAsync<IOException>(() => viewModel.ResetHeroStatusLayoutAsync(CancellationToken.None));

        Assert.Equal(MasterHeroStatusItemKind.ActiveConnections, viewModel.HeroStatusItems[0].Kind);
        Assert.Equal(store.SavedLayout, viewModel.HeroStatusItems.Select(static item => item.Kind));
        Assert.True(viewModel.HasOperationError);
    }
}
