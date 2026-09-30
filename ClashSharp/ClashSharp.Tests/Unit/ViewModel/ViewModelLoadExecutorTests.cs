using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Checks the shared page-load boundary, including stale results and fatal exception graphs.</summary>
public sealed class ViewModelLoadExecutorTests
{
    [Fact]
    public async Task SuccessfulRead_AppliesTheSnapshotAndReturnsTrue()
    {
        TestApplicationErrorSink sink = new();
        int applied = 0;

        bool result = await ViewModelLoadExecutor.ExecuteAsync(
            () => 42, value => applied = value, sink, "test-load", CancellationToken.None);

        Assert.True(result);
        Assert.Equal(42, applied);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task PreCancelledRead_DoesNotReadApplyOrReport()
    {
        TestApplicationErrorSink sink = new();
        bool read = false;
        bool applied = false;

        bool result = await ViewModelLoadExecutor.ExecuteAsync(
            () => { read = true; return 42; }, _ => applied = true, sink, "test-load", new CancellationToken(true));

        Assert.False(result);
        Assert.False(read);
        Assert.False(applied);
        Assert.Empty(sink.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverableFailure_ReportsOnceAndReturnsFalse(bool failWhileApplying)
    {
        TestApplicationErrorSink sink = new();
        IOException failure = new("Synthetic read or publication failure.");

        bool result = await ViewModelLoadExecutor.ExecuteAsync(
            () => failWhileApplying ? 42 : throw failure,
            _ => throw failure, sink, "test-load", CancellationToken.None);

        Assert.False(result);
        ApplicationError error = Assert.Single(sink.Errors);
        Assert.Equal("test-load", error.OperationName);
        Assert.Same(failure, error.Exception);
    }

    [Fact]
    public async Task UnrelatedCancellation_IsReportedAsFailure()
    {
        TestApplicationErrorSink sink = new();
        OperationCanceledException failure = new("Dependency cancelled itself.");

        bool result = await ViewModelLoadExecutor.ExecuteAsync<int>(
            () => throw failure, _ => { }, sink, "test-load", CancellationToken.None);

        Assert.False(result);
        Assert.Same(failure, Assert.Single(sink.Errors).Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRead_DoesNotApplyOrReport(bool failRead)
    {
        TestApplicationErrorSink sink = new();
        bool current = true;
        bool applied = false;

        bool result = await ViewModelLoadExecutor.ExecuteAsync(
            () => { current = false; return failRead ? throw new IOException("Old failure") : 42; },
            _ => applied = true, sink, "test-load", CancellationToken.None, () => current);

        Assert.False(result);
        Assert.False(applied);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public async Task FailingErrorSink_DoesNotTurnARecoverableFailureIntoAnUnhandledError()
    {
        bool result = await ViewModelLoadExecutor.ExecuteAsync<int>(
            () => throw new IOException("Read failure"), _ => { },
            new FailingSink(new InvalidOperationException("Sink unavailable")), "test-load", CancellationToken.None);

        Assert.False(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that fatal failures remain visible after cancellation or replacement.")]
    public async Task FatalReadGraph_StillEscapesAfterCancellationOrReplacement(bool callerCancelled)
    {
        using CancellationTokenSource cancellation = new();
        TestApplicationErrorSink sink = new();
        AggregateException fatal = new(new OperationCanceledException(), new OutOfMemoryException("Synthetic fatal failure"));

        AggregateException actual = await Assert.ThrowsAsync<AggregateException>(() =>
            ViewModelLoadExecutor.ExecuteAsync<int>(
                () => { if (callerCancelled) { cancellation.Cancel(); } throw fatal; },
                _ => { }, sink, "test-load", cancellation.Token, () => false));

        Assert.Same(fatal, actual);
        Assert.Empty(sink.Errors);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that the error reporting boundary does not swallow a fatal failure.")]
    public async Task FatalErrorSinkFailure_IsNotSwallowed()
    {
        OutOfMemoryException fatal = new("Synthetic fatal sink failure.");

        OutOfMemoryException actual = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            ViewModelLoadExecutor.ExecuteAsync<int>(
                () => throw new IOException("Read failure"), _ => { },
                new FailingSink(fatal), "test-load", CancellationToken.None));

        Assert.Same(fatal, actual);
    }

    private sealed class FailingSink(Exception failure) : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken) => Task.FromException(failure);
    }
}
