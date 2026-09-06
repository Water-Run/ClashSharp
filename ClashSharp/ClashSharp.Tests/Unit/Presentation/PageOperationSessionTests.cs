using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Tests.Unit.ViewModel;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class PageOperationSessionTests
{
    [Fact]
    public async Task RunAsync_QueuesAcceptedActionsInOrderWithoutCancellingTheFirst()
    {
        PageOperationSession session = new(new TestApplicationErrorSink(), "test-action");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> order = [];
        Task first = session.RunAsync(async token =>
        {
            order.Add(1);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            order.Add(2);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = session.RunAsync(_ => { order.Add(3); return Task.CompletedTask; });
        Task third = session.RunAsync(_ => { order.Add(4); return Task.CompletedTask; });
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1, 2, 3, 4], order);
    }

    [Fact]
    public async Task Cancel_RevokesRunningAndQueuedActionsAndAllowsANewVisit()
    {
        TestApplicationErrorSink errors = new();
        PageOperationSession session = new(errors, "test-action");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queuedInvoked = false;
        Task first = session.RunAsync(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = session.RunAsync(_ => { queuedInvoked = true; return Task.CompletedTask; });

        session.Cancel();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, queued);
        bool newVisitInvoked = false;
        await session.RunAsync(_ => { newVisitInvoked = true; return Task.CompletedTask; });

        Assert.False(queuedInvoked);
        Assert.True(newVisitInvoked);
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public async Task RunAsync_NewVisitWaitsForCancelledWorkToActuallyExit()
    {
        PageOperationSession session = new(new TestApplicationErrorSink(), "test-action");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool newVisitInvoked = false;
        Task first = session.RunAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Cancel();
        Task next = session.RunAsync(_ => { newVisitInvoked = true; return Task.CompletedTask; });
        Assert.False(newVisitInvoked);
        Assert.False(session.DrainAsync().IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(newVisitInvoked);
    }

    [Fact]
    public async Task RunAsync_UnexpectedFailureIsObservedAndDoesNotPoisonTheQueue()
    {
        TestApplicationErrorSink errors = new();
        PageOperationSession session = new(errors, "test-action");
        InvalidOperationException failure = new("Simulated platform failure.");
        Task failed = session.RunAsync(_ => Task.FromException(failure));
        bool nextInvoked = false;
        Task next = session.RunAsync(_ => { nextInvoked = true; return Task.CompletedTask; });

        await Task.WhenAll(failed, next);

        Assert.Same(failure, Assert.Single(errors.Errors).Exception);
        Assert.True(nextInvoked);
    }

    [Fact]
    public async Task RunAsync_ErrorSinkFailureDoesNotEscapeTheUiBoundary()
    {
        ThrowingErrorSink errors = new();
        PageOperationSession session = new(errors, "test-action");
        await session.RunAsync(_ => Task.FromException(new InvalidOperationException("Action failed.")));
        bool nextInvoked = false;
        await session.RunAsync(_ => { nextInvoked = true; return Task.CompletedTask; });

        Assert.Equal(1, errors.Calls);
        Assert.True(nextInvoked);
    }

    [Fact]
    public async Task Cancel_ObservesThrowingCallbacksAndStillDrainsTheQueue()
    {
        TestApplicationErrorSink errors = new();
        PageOperationSession session = new(errors, "test-action");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task action = session.RunAsync(async token =>
        {
            using CancellationTokenRegistration callback = token.Register(
                () => throw new InvalidOperationException("Cancellation callback failed."));
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Cancel();
        await session.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await action;

        Assert.IsType<AggregateException>(Assert.Single(errors.Errors).Exception);
    }

    private sealed class ThrowingErrorSink : IApplicationErrorSink
    {
        public int Calls { get; private set; }

        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Error presentation failed.");
        }
    }
}
