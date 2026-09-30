extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Presentation;
using Operations = ClashSharpUi::ClashSharp.Presentation.Lifecycle.PageOperationSession;
using Session = ClashSharpUi::ClashSharp.Presentation.Lifecycle.PageDataChangeSession;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class PageDataChangeSessionTests
{
    [Fact]
    public async Task PageDataChange_RevokesOldWorkBeforeTheUiQueueAndBlocksActionsUntilReloadCompletes()
    {
        Source source = new();
        Dispatcher ui = new();
        Errors errors = new();
        bool canceled = false;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Session session = new(source.Subscribe, ui.Post, [() => canceled = true], async _ =>
        {
            Assert.True(canceled);
            entered.SetResult();
            await release.Task;
        }, errors, "data-change");
        session.Start();

        source.Publish();

        Assert.True(canceled);
        Assert.True(session.IsInvalidated);
        Assert.False(entered.Task.IsCompleted);
        ui.RunAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsInvalidated);
        release.SetResult();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsInvalidated);
        Assert.Empty(errors.Reported);
        session.Stop();
    }

    [Fact]
    public async Task PageDataChange_ReloadWaitsForTheCanceledOperationToFinishItsAcceptedWork()
    {
        Source source = new();
        Dispatcher ui = new();
        Errors errors = new();
        Operations actions = new(errors, "old-operation");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        Task old = actions.RunAsync(async token => { oldToken = token; entered.SetResult(); await finish.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool reloaded = false;
        Session session = new(source.Subscribe, ui.Post, [actions.Cancel], async token =>
        {
            await actions.DrainAsync();
            token.ThrowIfCancellationRequested();
            reloaded = true;
        }, errors, "reload");
        session.Start();
        source.Publish();
        ui.RunAll();
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(old.IsCompleted);
        Assert.False(reloaded);
        finish.SetResult();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reloaded);
        Assert.True(old.IsCompleted);
        session.Stop();
    }

    [Fact]
    public async Task PageDataChange_OnlyTheLatestQueuedDirectoryCanRefreshThePage()
    {
        Source source = new();
        Dispatcher ui = new();
        int reloads = 0;
        Session session = new(source.Subscribe, ui.Post, [], _ => { reloads++; return Task.CompletedTask; }, new Errors(), "reload");
        session.Start();
        source.Publish();
        source.Publish();
        ui.RunAll();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reloads);
        Assert.False(session.IsInvalidated);
        session.Stop();
    }

    [Fact]
    public async Task PageDataChange_UnloadedAndReopenedVisitsCannotReceiveOldCallbacks()
    {
        Source source = new();
        Dispatcher ui = new();
        int reloads = 0;
        Session session = new(source.Subscribe, ui.Post, [], _ => { reloads++; return Task.CompletedTask; }, new Errors(), "reload");
        session.Start();
        Action oldCallback = source.Capture();
        source.Publish();
        session.Stop();
        session.Start();
        oldCallback();
        ui.RunAll();
        await session.DrainAsync();
        Assert.Equal(0, reloads);
        source.Publish();
        ui.RunAll();
        await session.DrainAsync();
        Assert.Equal(1, reloads);
        session.Stop();
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task PageDataChange_StopCancelsAndDrainsAnActiveRefresh()
    {
        Source source = new();
        Dispatcher ui = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Session session = new(source.Subscribe, ui.Post, [], async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { finished.SetResult(); }
        }, new Errors(), "reload");
        session.Start();
        source.Publish();
        ui.RunAll();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Stop();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Task.IsCompleted);
        Assert.Equal(0, source.Subscribers);
    }

    [Fact]
    public async Task PageDataChange_CancellationFailureDoesNotSkipOtherCancellationOrReload()
    {
        Source source = new();
        Dispatcher ui = new();
        Errors errors = new();
        IOException failure = new("cancel callback failed");
        bool canceled = false;
        bool reloaded = false;
        Session session = new(source.Subscribe, ui.Post, [() => throw failure, () => canceled = true],
            _ => { reloaded = true; return Task.CompletedTask; }, errors, "reload");
        session.Start();
        source.Publish();
        Assert.True(canceled);
        ui.RunAll();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reloaded);
        Assert.Same(failure, Assert.Single(errors.Reported));
        session.Stop();
    }

    [Fact]
    public async Task PageDataChange_FailedRefreshKeepsOldActionsInvalidated()
    {
        Source source = new();
        Dispatcher ui = new();
        Errors errors = new();
        Session session = new(source.Subscribe, ui.Post, [], _ => throw new IOException("load unavailable"), errors, "reload");
        session.Start();
        source.Publish();
        ui.RunAll();
        await session.DrainAsync();
        Assert.True(session.IsInvalidated);
        Assert.Single(errors.Reported);
        session.Stop();
    }

    [Fact]
    public async Task PageDataChange_RejectedUiDispatchStillRevokesOldActions()
    {
        Source source = new();
        bool canceled = false;
        Session session = new(source.Subscribe, _ => false, [() => canceled = true],
            _ => throw new InvalidOperationException("UI callback was not accepted."), new Errors(), "reload");
        session.Start();
        source.Publish();
        Assert.True(canceled);
        Assert.True(session.IsInvalidated);
        session.Stop();
        await session.DrainAsync();
    }

    private sealed class Source
    {
        private event Action? Changed;
        public int Subscribers { get; private set; }
        public IDisposable Subscribe(Action callback) { Changed += callback; Subscribers++; return new Subscription(this, callback); }
        public Action Capture() => Changed ?? (() => { });
        public void Publish() => Changed?.Invoke();
        private sealed class Subscription(Source owner, Action callback) : IDisposable
        {
            private bool _disposed;
            public void Dispose() { if (_disposed) { return; } _disposed = true; owner.Changed -= callback; owner.Subscribers--; }
        }
    }

    private sealed class Dispatcher
    {
        private readonly Queue<Action> _callbacks = new();
        public bool Post(Action callback) { _callbacks.Enqueue(callback); return true; }
        public void RunAll() { while (_callbacks.TryDequeue(out Action? callback)) { callback(); } }
    }

    private sealed class Errors : IApplicationErrorSink
    {
        public List<Exception> Reported { get; } = [];
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) { Reported.Add(error.Exception); return Task.CompletedTask; }
    }
}
