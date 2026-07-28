extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Startup;
using PersistentStartupDiagnosticSink =
    ClashSharpUi::ClashSharp.Hosting.Startup.PersistentStartupDiagnosticSink;
using StartupExceptionDiagnostics =
    ClashSharpUi::ClashSharp.Hosting.Startup.StartupExceptionDiagnostics;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies durable startup diagnostics survive primary log storage failures.</summary>
public sealed class PersistentStartupDiagnosticSinkTests
{
    /// <summary>Verifies construction alone neither starts persistence nor accepts records.</summary>
    [Fact]
    public async Task Constructor_WithoutExplicitStart_PerformsNoBackgroundPersistence()
    {
        int writes = 0;
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) => Interlocked.Increment(ref writes),
            _ => Interlocked.Increment(ref writes));

        Assert.Throws<InvalidOperationException>(() => sink.Record(CreateRecord("not-started")));
        await sink.CompleteAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.Throws<InvalidOperationException>(sink.Start);
    }

    /// <summary>Verifies the owner can idempotently start exactly one consumer.</summary>
    [Fact]
    public async Task Start_RepeatedCall_UsesOneOrderedConsumer()
    {
        List<string> messages = [];
        PersistentStartupDiagnosticSink sink = new(
            (_, _, message, _) => messages.Add(message),
            _ => { });

        sink.Start();
        sink.Start();
        sink.Record(CreateRecord("started"));
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Startup step 'started' started."], messages);
    }

    /// <summary>Verifies blocked persistence never blocks the caller that records startup progress.</summary>
    [Fact]
    public async Task Record_WriterBlocks_ReturnsWithoutWaitingForPersistence()
    {
        using ManualResetEventSlim writerStarted = new();
        using ManualResetEventSlim releaseWriter = new();
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) =>
            {
                writerStarted.Set();
                releaseWriter.Wait();
            },
            _ => { });
        sink.Start();
        StartupDiagnosticRecord record = CreateRecord("first");

        Task recordCall = Task.Run(() => sink.Record(record));
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(recordCall, Task.Delay(TimeSpan.FromMilliseconds(250)));

            Assert.Same(recordCall, completed);
        }
        finally
        {
            releaseWriter.Set();
            await recordCall;
            await sink.CompleteAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Verifies shutdown diagnostics use the same non-blocking owned queue as startup records.</summary>
    [Fact]
    public async Task RecordLifecycleFailure_WriterBlocks_ReturnsWithoutWaitingForPersistence()
    {
        using ManualResetEventSlim writerStarted = new();
        using ManualResetEventSlim releaseWriter = new();
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) =>
            {
                writerStarted.Set();
                releaseWriter.Wait();
            },
            _ => { });
        sink.Start();

        Task recordCall = Task.Run(
            () => sink.RecordLifecycleFailure(
                "Application shutdown failed.",
                new InvalidOperationException("host stop failed")));
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(
                recordCall,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(recordCall, completed);
        }
        finally
        {
            releaseWriter.Set();
            await recordCall;
            await sink.CompleteAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Verifies App-owned disposal retains a finite bound when lifecycle persistence is stuck.</summary>
    [Fact]
    public async Task DisposeAsync_LifecycleWriterBlocks_TimesOutWithinConfiguredBound()
    {
        using ManualResetEventSlim writerStarted = new();
        using ManualResetEventSlim releaseWriter = new();
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) =>
            {
                writerStarted.Set();
                releaseWriter.Wait();
            },
            _ => { },
            TimeSpan.FromMilliseconds(100));
        sink.Start();
        sink.RecordLifecycleFailure("Application shutdown failed.", null);
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => sink.DisposeAsync().AsTask());
        }
        finally
        {
            releaseWriter.Set();
        }

        await sink.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies lifecycle detail never calls hostile exception formatting.</summary>
    [Fact]
    public async Task RecordLifecycleFailure_ExceptionTextThrows_PersistsStableType()
    {
        string? detail = null;
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, writtenDetail) => detail = writtenDetail,
            _ => { });
        sink.Start();

        Exception? failure = Record.Exception(
            () => sink.RecordLifecycleFailure(
                "Application shutdown failed.",
                new ThrowingMessageException()));
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));

        Assert.Null(failure);
        Assert.NotNull(detail);
        Assert.Contains(nameof(ThrowingMessageException), detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies exception-controlled text is never read on the shutdown caller.</summary>
    [Fact]
    public async Task RecordLifecycleFailure_ExceptionMessageBlocks_ReturnsBeforeMessageIsReleased()
    {
        using ManualResetEventSlim messageReadStarted = new();
        using ManualResetEventSlim releaseMessage = new();
        PersistentStartupDiagnosticSink sink = new((_, _, _, _) => { }, _ => { });
        sink.Start();
        BlockingMessageException exception = new(messageReadStarted, releaseMessage);

        Task recordCall = Task.Run(
            () => sink.RecordLifecycleFailure("Application shutdown failed.", exception));
        Assert.True(messageReadStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(
                recordCall,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(recordCall, completed);
        }
        finally
        {
            releaseMessage.Set();
            await recordCall;
            await sink.CompleteAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Verifies startup-failure exception text is also deferred to the owned consumer.</summary>
    [Fact]
    public async Task RecordFailure_ExceptionMessageBlocks_ReturnsBeforeMessageIsReleased()
    {
        using ManualResetEventSlim messageReadStarted = new();
        using ManualResetEventSlim releaseMessage = new();
        PersistentStartupDiagnosticSink sink = new((_, _, _, _) => { }, _ => { });
        sink.Start();
        BlockingMessageException exception = new(messageReadStarted, releaseMessage);
        StartupDiagnosticRecord record = new(
            "network-behavior",
            450,
            StartupDiagnosticStage.Failed,
            null,
            null,
            TimeSpan.Zero,
            exception.GetType().FullName,
            null);

        Task recordCall = Task.Run(() => sink.RecordFailure(record, exception));
        Assert.True(messageReadStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Task completed = await Task.WhenAny(
                recordCall,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(recordCall, completed);
        }
        finally
        {
            releaseMessage.Set();
            await recordCall;
            await sink.CompleteAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Verifies a failed database append falls back to a complete text diagnostic.</summary>
    [Fact]
    public async Task Record_LogStorageThrows_WritesFallbackDiagnostic()
    {
        string? fallbackLine = null;
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) => throw new IOException("database unavailable"),
            line => fallbackLine = line);
        sink.Start();
        StartupDiagnosticRecord record = new(
            "startup-network-behavior",
            450,
            StartupDiagnosticStage.Failed,
            null,
            "startup-unhandled-exception",
            TimeSpan.FromMilliseconds(125),
            typeof(InvalidOperationException).FullName,
            "notification unavailable");

        sink.Record(record);
        await sink.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(fallbackLine);
        Assert.Contains("Startup step 'startup-network-behavior' failed.", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("order=450", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=125.000", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("notification unavailable", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException", fallbackLine, StringComparison.Ordinal);
        Assert.Contains("database unavailable", fallbackLine, StringComparison.Ordinal);
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies a hostile exception cannot escape the best-effort unhandled diagnostic boundary.</summary>
    [Fact]
    public async Task RecordUnhandled_ExceptionMessageThrows_DoesNotReplaceStartupFailure()
    {
        string? detail = null;
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, writtenDetail) => detail = writtenDetail,
            _ => throw new InvalidOperationException("fallback should not be used"));
        sink.Start();

        Exception? diagnosticFailure = Record.Exception(
            () => sink.RecordUnhandled(new ThrowingMessageException()));
        await sink.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.Null(diagnosticFailure);
        Assert.NotNull(detail);
        Assert.Contains(nameof(ThrowingMessageException), detail, StringComparison.Ordinal);
        Assert.Contains("exceptionMessage=", detail, StringComparison.Ordinal);
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies the single consumer preserves the order accepted by Record.</summary>
    [Fact]
    public async Task CompleteAsync_MultipleRecords_PersistsInFifoOrder()
    {
        List<string> writtenMessages = [];
        PersistentStartupDiagnosticSink sink = new(
            (_, _, message, _) => writtenMessages.Add(message),
            _ => { });
        sink.Start();

        sink.Record(CreateRecord("first"));
        sink.Record(CreateRecord("second"));
        sink.Record(CreateRecord("third"));
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "Startup step 'first' started.",
                "Startup step 'second' started.",
                "Startup step 'third' started.",
            ],
            writtenMessages);
    }

    /// <summary>Verifies flush does not complete until all earlier records finish persistence.</summary>
    [Fact]
    public async Task FlushAsync_WriterBlocks_WaitsForEarlierRecord()
    {
        using ManualResetEventSlim writerStarted = new();
        using ManualResetEventSlim releaseWriter = new();
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) =>
            {
                writerStarted.Set();
                releaseWriter.Wait();
            },
            _ => { });
        sink.Start();
        sink.Record(CreateRecord("blocked"));
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        Task flush = sink.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.False(flush.IsCompleted);

        releaseWriter.Set();
        await flush;
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies completion has an explicit bound even when synchronous persistence is stuck.</summary>
    [Fact]
    public async Task CompleteAsync_WriterBlocks_TimesOutWithinBound()
    {
        using ManualResetEventSlim writerStarted = new();
        using ManualResetEventSlim releaseWriter = new();
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) =>
            {
                writerStarted.Set();
                releaseWriter.Wait();
            },
            _ => { });
        sink.Start();
        sink.Record(CreateRecord("blocked"));
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => sink.CompleteAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseWriter.Set();
        }

        await sink.CompleteAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies records cannot be silently accepted after the queue is completed.</summary>
    [Fact]
    public async Task Record_AfterComplete_ThrowsInsteadOfDroppingRecord()
    {
        PersistentStartupDiagnosticSink sink = new((_, _, _, _) => { }, _ => { });
        sink.Start();
        await sink.CompleteAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(() => sink.Record(CreateRecord("late")));
    }

    /// <summary>Verifies both persistence faults are observable and do not abandon later queue entries.</summary>
    [Fact]
    public async Task CompleteAsync_BothWritersFail_ReportsFaultAndContinuesConsumer()
    {
        int primaryAttempt = 0;
        List<string> laterMessages = [];
        PersistentStartupDiagnosticSink sink = new(
            (_, _, message, _) =>
            {
                if (Interlocked.Increment(ref primaryAttempt) == 1)
                {
                    throw new IOException("database unavailable");
                }

                laterMessages.Add(message);
            },
            _ => throw new UnauthorizedAccessException("fallback unavailable"));
        sink.Start();
        sink.Record(CreateRecord("failed"));
        sink.Record(CreateRecord("later"));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => sink.CompleteAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(exception.InnerExceptions, error => error is IOException);
        Assert.Contains(exception.InnerExceptions, error => error is UnauthorizedAccessException);
        Assert.Equal(["Startup step 'later' started."], laterMessages);
    }

    /// <summary>Verifies cancellation from persistence faults the observable consumer completion.</summary>
    [Fact]
    public async Task CompleteAsync_WriterCancels_PropagatesCancellation()
    {
        PersistentStartupDiagnosticSink sink = new(
            (_, _, _, _) => throw new OperationCanceledException("writer canceled"),
            _ => throw new InvalidOperationException("fallback must not run"));
        sink.Start();
        sink.Record(CreateRecord("canceled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.CompleteAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Verifies App debug reporting never formats a hostile exception instance.</summary>
    [Fact]
    public void FormatDebugMessage_ExceptionFormattingThrows_UsesOnlyStableType()
    {
        ThrowingMessageException exception = new();

        string message = StartupExceptionDiagnostics.FormatDebugMessage(exception);

        Assert.StartsWith("ClashSharp operation failed", message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingMessageException), message, StringComparison.Ordinal);
        Assert.DoesNotContain("message unavailable", message, StringComparison.Ordinal);
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("message unavailable");

        public override string ToString() => throw new InvalidOperationException("formatting unavailable");
    }

    private sealed class BlockingMessageException(
        ManualResetEventSlim messageReadStarted,
        ManualResetEventSlim releaseMessage) : Exception
    {
        public override string Message
        {
            get
            {
                messageReadStarted.Set();
                releaseMessage.Wait();
                return "message released";
            }
        }

        public override string ToString() => throw new InvalidOperationException(
            "formatting must not be used");
    }

    private static StartupDiagnosticRecord CreateRecord(string stepName)
    {
        return new StartupDiagnosticRecord(
            stepName,
            100,
            StartupDiagnosticStage.Started,
            null,
            null,
            TimeSpan.Zero,
            null,
            null);
    }
}
