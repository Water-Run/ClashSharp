using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class LogsViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchRuntimeLogsAsync_TransportCancellationRetriesWithoutPageCancellation(bool serviceHost)
    {
        int attempts = 0;
        Task AttemptAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ++attempts == 1
                ? Task.FromException(new OperationCanceledException("Transport deadline exceeded."))
                : Task.CompletedTask;
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(),
            streamRuntimeLogs: serviceHost ? null : token => StreamAfterAsync(AttemptAsync, token),
            readServiceHostLogs: serviceHost ? async token =>
            {
                await AttemptAsync(token);
                return ["recovered"];
            }
        : null);
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogsViewModel.RecentLogs))
            {
                published.TrySetResult();
            }
        };
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        try
        {
            Assert.False(watch.IsCompleted);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, attempts);
            Assert.Equal(serviceHost ? "Service" : "Core", Assert.Single(viewModel.RecentLogs).Record.Source);
            Assert.Equal("recovered", viewModel.RecentLogs[0].Message);
        }
        finally
        {
            lifetime.Cancel();
            _ = await Record.ExceptionAsync(() => watch);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that wrapped process-fatal failures are not swallowed by reconnect logic.")]
    public async Task WatchRuntimeLogsAsync_WrappedFatalFailureDoesNotReconnect(bool serviceHost, bool fatalCancellation)
    {
        Exception failure = fatalCancellation
            ? new OperationCanceledException("Transport failed during cancellation.", new OutOfMemoryException())
            : new IOException("Transport failed.", new OutOfMemoryException());
        int attempts = 0;
        Task FailAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            attempts++;
            return Task.FromException(failure);
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(),
            streamRuntimeLogs: serviceHost ? null : token => StreamAfterAsync(FailAsync, token),
            readServiceHostLogs: serviceHost ? async token =>
            {
                await FailAsync(token);
                return Array.Empty<string>();
            }
        : null);
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        try
        {
            if (fatalCancellation)
            {
                AggregateException observed = await Assert.ThrowsAsync<AggregateException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains(failure, observed.Flatten().InnerExceptions);
            }
            else
            {
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5))));
            }
            Assert.True(watch.IsFaulted);
            Assert.Equal(1, attempts);
        }
        finally
        {
            lifetime.Cancel();
            _ = await Record.ExceptionAsync(() => watch);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchRuntimeLogsAsync_UnexpectedFailureCancelsAndDrainsTheOtherSource(bool failingCore)
    {
        FormatException failure = new("Unexpected source failure.");
        TaskCompletionSource fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReadAsync(bool failing, CancellationToken token)
        {
            if (failing)
            {
                await fail.Task;
                throw failure;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cancellationObserved.TrySetResult();
                await releaseCleanup.Task;
            }
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(),
            token => StreamAfterAsync(innerToken => ReadAsync(failingCore, innerToken), token),
            async token =>
            {
                await ReadAsync(!failingCore, token);
                return Array.Empty<string>();
            });
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        try
        {
            fail.SetResult();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lifetime.IsCancellationRequested);
            Assert.False(watch.IsCompleted);
            releaseCleanup.SetResult();
            Assert.Same(failure, await Assert.ThrowsAsync<FormatException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5))));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            lifetime.Cancel();
            _ = await Record.ExceptionAsync(() => watch);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that a second fatal failure remains visible alongside the first ordinary failure.")]
    public async Task WatchRuntimeLogsAsync_ConcurrentFailuresPreserveTheFatalExceptionGraph(bool fatalCancellation)
    {
        FormatException coreFailure = new("Unexpected core failure.");
        Exception hostFailure = fatalCancellation
            ? new OperationCanceledException("Host failure during cancellation.", new OutOfMemoryException())
            : new IOException("Host failure.", new OutOfMemoryException());
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReadCoreAsync(CancellationToken token)
        {
            await release.Task;
            throw coreFailure;
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(),
            token => StreamAfterAsync(ReadCoreAsync, token),
            async _ =>
            {
                await release.Task;
                throw hostFailure;
            });
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        try
        {
            release.SetResult();
            AggregateException observed = await Assert.ThrowsAsync<AggregateException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(coreFailure, observed.Flatten().InnerExceptions);
            Assert.Contains(hostFailure, observed.Flatten().InnerExceptions);
            Assert.True(ExceptionGraphClassifier.IsProcessFatal(observed));
        }
        finally
        {
            lifetime.Cancel();
            _ = await Record.ExceptionAsync(() => watch);
        }
    }

    [Fact]
    public async Task WatchRuntimeLogsAsync_CancellationCallbackFailureDoesNotHideTheSourceFailure()
    {
        FormatException sourceFailure = new("Unexpected core failure.");
        IOException callbackFailure = new("Cancellation callback failed.");
        TaskCompletionSource fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReadCoreAsync(CancellationToken token)
        {
            await fail.Task;
            throw sourceFailure;
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(),
            token => StreamAfterAsync(ReadCoreAsync, token),
            async token =>
            {
                using CancellationTokenRegistration registration = token.Register(() =>
                {
                    callbackInvoked.TrySetResult();
                    throw callbackFailure;
                });
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    await callbackInvoked.Task;
                }
                return Array.Empty<string>();
            });
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        try
        {
            fail.SetResult();
            AggregateException observed = await Assert.ThrowsAsync<AggregateException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(sourceFailure, observed.Flatten().InnerExceptions);
            Assert.Contains(callbackFailure, observed.Flatten().InnerExceptions);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => lifetime.CancelAsync());
            _ = await Record.ExceptionAsync(() => watch);
        }
    }

    [Fact]
    public async Task WatchRuntimeLogsAsync_PageCancellationDisposesBothSourcesAndRejectsLateRows()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool coreDisposed = false;
        bool hostReturned = false;
        async IAsyncEnumerable<(string Level, string Message)> CoreAsync([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await release.Task;
                yield return ("Info", "late core row");
            }
            finally
            {
                coreDisposed = true;
            }
        }

        LogsViewModel viewModel = new(
            GetString, new FakeLogManagementStore(), new TestApplicationErrorSink(), CoreAsync,
            async _ =>
            {
                await release.Task;
                hostReturned = true;
                return ["late host row"];
            });
        using CancellationTokenSource lifetime = new();
        Task watch = viewModel.WatchRuntimeLogsAsync(lifetime.Token);
        lifetime.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(coreDisposed);
        Assert.True(hostReturned);
        Assert.Empty(viewModel.RecentLogs);
    }

    private static async IAsyncEnumerable<(string Level, string Message)> StreamAfterAsync(
        Func<CancellationToken, Task> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await operation(cancellationToken);
        yield return ("Info", "recovered");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
