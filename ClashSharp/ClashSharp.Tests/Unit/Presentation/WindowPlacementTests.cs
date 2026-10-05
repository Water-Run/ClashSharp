using System.Text.Json;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Infrastructure.Settings;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class WindowPlacementTests
{
    private static readonly WindowPlacementBounds OriginalArea = new(0, 0, 1920, 1040);
    private static WindowPlacementState Saved(bool maximized = false) => new(1, "DISPLAY1", new(100, 100, 1000, 700), OriginalArea, 96, maximized);

    [Theory]
    [InlineData(96u, 100, 1000, 700)]
    [InlineData(144u, 150, 1500, 1050)]
    [InlineData(192u, 200, 2000, 1400)]
    public void DpiChangePreservesLogicalSizeAndPosition(uint dpi, int offset, int width, int height)
    {
        var monitor = new WindowPlacementMonitor("DISPLAY1", new(0, 0, 3840, 2080));

        WindowPlacementBounds restored = WindowPlacementPolicy.Resolve(Saved(), monitor, dpi);

        Assert.Equal(new(offset, offset, width, height), restored);
    }

    [Fact]
    public void NegativeMonitorOriginsAndTopTaskbarOffsetsDoNotCausePositionDrift()
    {
        var state = new WindowPlacementState(1, "LEFT", new(-1800, 90, 1000, 700), new(-1920, 40, 1920, 1040), 96, false);
        var monitor = new WindowPlacementMonitor("LEFT", new(-2560, 60, 2560, 1380));

        WindowPlacementBounds restored = WindowPlacementPolicy.Resolve(state, monitor, 144);

        Assert.Equal(new(-2380, 135, 1500, 1050), restored);
    }

    [Fact]
    public void RemovedMonitorFallsBackToAVisibleCenteredWindow()
    {
        var state = Saved() with { MonitorName = "REMOVED", NormalBounds = new(5000, 300, 1000, 700) };
        var primary = new WindowPlacementMonitor("PRIMARY", OriginalArea);
        var left = new WindowPlacementMonitor("LEFT", new(-1920, 0, 1920, 1040));

        WindowPlacementMonitor selected = WindowPlacementPolicy.SelectMonitor(state, [left, primary]);
        WindowPlacementBounds restored = WindowPlacementPolicy.Resolve(state, selected, 96);

        Assert.Same(primary, selected);
        Assert.Equal(new(460, 170, 1000, 700), restored);
    }

    [Fact]
    public void MatchingDeviceIsPreferredEvenAfterDesktopOriginsChange()
    {
        var moved = new WindowPlacementMonitor("DISPLAY1", new(4000, -500, 1920, 1040));
        Assert.Same(moved, WindowPlacementPolicy.SelectMonitor(Saved(), [new("OTHER", OriginalArea), moved]));
        Assert.Equal(new(4100, -400, 1000, 700), WindowPlacementPolicy.Resolve(Saved(), moved, 96));
    }

    [Theory]
    [InlineData(0, 0, 640, 480, 192u)]
    [InlineData(-1280, 50, 1280, 670, 144u)]
    public void SmallWorkAreasOverrideTheLogicalMinimumToKeepControlsReachable(int x, int y, int width, int height, uint dpi)
    {
        var monitor = new WindowPlacementMonitor("DISPLAY1", new(x, y, width, height));
        WindowPlacementBounds restored = WindowPlacementPolicy.Resolve(Saved() with { NormalBounds = new(10000, -10000, 9000, 9000) }, monitor, dpi);
        Assert.Equal(monitor.WorkArea, restored);
    }

    [Fact]
    public void MaximizingThenMinimizingOrHidingRetainsNormalBoundsAndMaximizedMode()
    {
        var tracker = new WindowPlacementTracker();
        var monitor = new WindowPlacementMonitor("DISPLAY1", OriginalArea);
        tracker.Observe(monitor, Saved().NormalBounds, 96, true, false, false);
        tracker.Observe(monitor, OriginalArea, 96, true, false, true);
        WindowPlacementState maximized = Assert.IsType<WindowPlacementState>(tracker.Current);
        tracker.Observe(monitor, new(-32000, -32000, 1, 1), 96, true, true, false);
        tracker.Observe(monitor, new(-32000, -32000, 1, 1), 96, false, false, false);

        Assert.Equal(Saved(true), maximized);
        Assert.Equal(maximized, tracker.Current);
        tracker.Observe(monitor, new(200, 120, 1200, 800), 96, true, false, false);
        Assert.False(tracker.Current!.IsMaximized);
        Assert.Equal(new(200, 120, 1200, 800), tracker.Current.NormalBounds);
    }

    [Fact]
    public void MovingAMaximizedWindowKeepsItsLogicalNormalSizeOnTheNewMonitor()
    {
        var tracker = new WindowPlacementTracker();
        tracker.Seed(Saved(true));
        var monitor = new WindowPlacementMonitor("SECOND", new(1920, 0, 2560, 1400));

        tracker.Observe(monitor, monitor.WorkArea, 144, true, false, true);

        Assert.True(tracker.Current!.IsMaximized);
        Assert.Equal("SECOND", tracker.Current.MonitorName);
        Assert.Equal(new(2450, 175, 1500, 1050), tracker.Current.NormalBounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreRoundTripsCompleteStateAndLeavesNoTemporaryFiles(bool maximized)
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonWindowPlacementStore(directory.Path);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        Assert.False(Directory.Exists(directory.Path));

        await store.SaveAsync(Saved(maximized), CancellationToken.None);

        Assert.Equal(Saved(maximized), await store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversize")]
    public async Task MalformedOrUnboundedDocumentsAreRejectedWithoutChangingThem(string problem)
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonWindowPlacementStore(directory.Path);
        await store.SaveAsync(Saved(), CancellationToken.None);
        string path = System.IO.Path.Combine(directory.Path, "WindowState", "v1", "placement.json");
        string original = await File.ReadAllTextAsync(path);
        string changed = problem switch
        {
            "schema" => original.Replace("\"schema\":1", "\"schema\":9", StringComparison.Ordinal),
            "duplicate" => original.Insert(1, "\"schema\":1,"),
            "unknown" => original.Insert(1, "\"extra\":true,"),
            _ => new string(' ', 4097),
        };
        await File.WriteAllTextAsync(path, changed);

        Exception? failure = await Record.ExceptionAsync(() => store.LoadAsync(CancellationToken.None));

        Assert.True(failure is ArgumentException or JsonException or InvalidDataException);
        Assert.Equal(changed, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PrecancelledSaveKeepsTheLastDurablePlacement()
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonWindowPlacementStore(directory.Path);
        await store.SaveAsync(Saved(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Saved(true), cancellation.Token));

        Assert.Equal(Saved(), await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LocalDataClearWaitsForEarlierCheckpointAndDoesNotWriteAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Save = async _ => { entered.TrySetResult(); await finish.Task; } };
        var view = new View { Current = Saved() };
        var session = new WindowPlacementSession(store, view, new Errors());
        await session.InitializeAsync(CancellationToken.None);
        Task saving = session.CheckpointAsync(true, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task clearing = session.PrepareShutdownAsync(false, CancellationToken.None);
        Task lateHide = session.CheckpointAsync(true, CancellationToken.None);
        try { Assert.False(clearing.IsCompleted); }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(saving, clearing, lateHide);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task FailedShutdownReopensGeometryCheckpoints()
    {
        var store = new Store();
        var view = new View { Current = Saved() };
        var session = new WindowPlacementSession(store, view, new Errors());
        await session.InitializeAsync(CancellationToken.None);
        await session.PrepareShutdownAsync(true, CancellationToken.None);
        view.Current = Saved(true);
        await session.CheckpointAsync(true, CancellationToken.None);
        Assert.Equal(Saved(), store.LastSaved);

        session.ResumeAfterShutdownFailure();
        await session.CheckpointAsync(true, CancellationToken.None);

        Assert.Equal(Saved(true), store.LastSaved);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public async Task DelayedLoadDoesNotOverwriteAUsersNewWindowPlacement()
    {
        var loaded = new TaskCompletionSource<WindowPlacementState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Load = _ => loaded.Task };
        var view = new View { Current = Saved() };
        var session = new WindowPlacementSession(store, view, new Errors());
        Task initializing = session.InitializeAsync(CancellationToken.None);
        view.Current = Saved() with { NormalBounds = new(400, 200, 1100, 800) };
        loaded.SetResult(Saved(true));

        await initializing;
        await session.CheckpointAsync(true, CancellationToken.None);

        Assert.Equal(0, view.Restores);
        Assert.Equal(view.Current, store.LastSaved);
    }

    [Fact]
    public async Task CorruptLoadAndFailedSaveAreReportedWithoutBlockingWindowLifetime()
    {
        var errors = new Errors();
        var store = new Store { Load = _ => throw new InvalidDataException(), Save = _ => throw new IOException() };
        var session = new WindowPlacementSession(store, new View { Current = Saved() }, errors);

        await session.InitializeAsync(CancellationToken.None);
        await session.CheckpointAsync(true, CancellationToken.None);

        Assert.Equal(["window-placement-load", "window-placement-save"], errors.Observed.Select(error => error.OperationName));
    }

    [Fact]
    public async Task InitialPlacementIsLoadedOnceAndAReadOnlyCheckpointDoesNotPersist()
    {
        var store = new Store { Load = _ => Task.FromResult<WindowPlacementState?>(Saved(true)) };
        var view = new View { Current = Saved() };
        var session = new WindowPlacementSession(store, view, new Errors());

        await session.InitializeAsync(CancellationToken.None);
        await session.InitializeAsync(CancellationToken.None);
        await session.CheckpointAsync(false, CancellationToken.None);

        Assert.Equal(1, store.Loads);
        Assert.Equal(1, view.Restores);
        Assert.Equal(Saved(true), view.Current);
        Assert.Equal(0, store.Saves);
    }

    private sealed class View : IWindowPlacementView
    {
        internal WindowPlacementState? Current { get; set; }
        internal int Restores { get; private set; }
        public void Observe() { }
        public WindowPlacementState? Capture() => Current;
        public void Restore(WindowPlacementState state) { Restores++; Current = state; }
        public (int Width, int Height) ConstrainMinimumSize(int width, int height) => (width, height);
    }
    private sealed class Store : IWindowPlacementStore
    {
        internal Func<CancellationToken, Task<WindowPlacementState?>> Load { get; init; } = _ => Task.FromResult<WindowPlacementState?>(null);
        internal Func<CancellationToken, Task> Save { get; init; } = _ => Task.CompletedTask;
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        internal WindowPlacementState? LastSaved { get; private set; }
        public Task<WindowPlacementState?> LoadAsync(CancellationToken cancellationToken) { Loads++; return Load(cancellationToken); }
        public Task SaveAsync(WindowPlacementState state, CancellationToken cancellationToken) { Saves++; LastSaved = state; return Save(cancellationToken); }
    }
    private sealed class Errors : IApplicationErrorSink
    {
        internal List<ApplicationError> Observed { get; } = [];
        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken) { Observed.Add(applicationError); return Task.CompletedTask; }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClashSharp.WindowPlacement." + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
    }
}
