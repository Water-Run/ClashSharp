using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Persists queued startup and application-lifecycle diagnostics with a text fallback.</summary>
/// <remarks>
/// The application object owns this sink. The DI host receives the externally-created instance and
/// must not dispose it. The queue is intentionally unbounded because startup emits few records and
/// losing a fatal diagnostic is worse than the small, process-lifetime memory bound.
/// </remarks>
internal sealed class PersistentStartupDiagnosticSink : IStartupDiagnosticSink, IAsyncDisposable
{
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(2);
    private const string LogSource = "StartupPipeline";
    private const string FallbackFileName = "StartupDiagnostics.log";
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Action<string> _appendFallback;
    private readonly TimeSpan _disposeTimeout;
    private readonly Channel<QueueItem> _records;
    private readonly object _lifecycleLock = new();
    private readonly List<Exception> _persistenceFailures = [];
    private Task? _consumer;
    private Task? _consumerObservation;
    private bool _completed;

    /// <summary>Creates the production sink backed by SQLite with a local text fallback.</summary>
    public PersistentStartupDiagnosticSink()
        : this(
            (level, source, message, detail) =>
                LogStorageService.Instance.AppendLog(level, source, message, detail),
            AppendFallbackFile)
    {
    }

    /// <summary>Creates a sink around explicit persistence boundaries.</summary>
    internal PersistentStartupDiagnosticSink(
        Action<string, string, string, string?> appendLog,
        Action<string> appendFallback)
        : this(appendLog, appendFallback, DefaultDisposeTimeout)
    {
    }

    /// <summary>Creates a sink with an explicit owner-disposal bound.</summary>
    internal PersistentStartupDiagnosticSink(
        Action<string, string, string, string?> appendLog,
        Action<string> appendFallback,
        TimeSpan disposeTimeout)
    {
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _appendFallback = appendFallback ?? throw new ArgumentNullException(nameof(appendFallback));
        ValidateTimeout(disposeTimeout);
        _disposeTimeout = disposeTimeout;
        _records = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Starts the one application-owned persistence consumer.</summary>
    /// <remarks>
    /// Construction is side-effect free. The primary-instance callback starts the sink after
    /// ownership is confirmed, so redirected secondary processes never create this background task.
    /// Repeated calls while started are idempotent.
    /// </remarks>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Startup diagnostic recording has already completed.");
            }

            if (_consumer is not null)
            {
                return;
            }

            Task consumer = Task.Run(ConsumeAsync);
            _consumer = consumer;
            _consumerObservation = consumer.ContinueWith(
                static task =>
                {
                    if (task.IsFaulted)
                    {
                        _ = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <inheritdoc />
    public void Record(StartupDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Enqueue(CreateStartupDiagnostic(record, null));
    }

    /// <inheritdoc />
    public void RecordFailure(StartupDiagnosticRecord record, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(exception);
        Enqueue(CreateStartupDiagnostic(record, exception));
    }

    private static PersistentDiagnostic CreateStartupDiagnostic(
        StartupDiagnosticRecord record,
        Exception? exception)
    {
        string level = record.Stage == StartupDiagnosticStage.Failed
            || record.Outcome == StartupStepOutcome.Fatal
                ? "Error"
                : "Info";
        string message = record.Stage switch
        {
            StartupDiagnosticStage.Started => $"Startup step '{record.StepName}' started.",
            StartupDiagnosticStage.Completed => $"Startup step '{record.StepName}' completed.",
            StartupDiagnosticStage.Failed => $"Startup step '{record.StepName}' failed.",
            _ => $"Startup step '{record.StepName}' changed state.",
        };
        string detail = exception is null
            ? FormatDetail(record)
            : FormatDetailWithoutException(record);
        return new PersistentDiagnostic(
            level,
            LogSource,
            message,
            detail,
            exception);
    }

    /// <summary>Queues an application-lifecycle failure without invoking the persistence writer inline.</summary>
    /// <param name="message">Stable lifecycle message to persist. Not null.</param>
    /// <param name="exception">Optional failure whose stable type and best-effort message are captured.</param>
    public void RecordLifecycleFailure(string message, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(message);
        Enqueue(new PersistentDiagnostic(
            "Error",
            "ApplicationLifecycle",
            message,
            null,
            exception));
    }

    private void Enqueue(PersistentDiagnostic diagnostic)
    {
        lock (_lifecycleLock)
        {
            if (_consumer is null)
            {
                throw new InvalidOperationException(
                    "Startup diagnostic recording must be started by its application owner.");
            }
        }

        if (!_records.Writer.TryWrite(QueueItem.ForDiagnostic(diagnostic)))
        {
            throw new InvalidOperationException("Startup diagnostic recording has completed.");
        }
    }

    /// <summary>Waits, within a caller-supplied bound, until all previously accepted records are handled.</summary>
    public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        Task consumer;
        Task consumerObservation;
        lock (_lifecycleLock)
        {
            consumer = _consumer
                ?? throw new InvalidOperationException(
                    "Startup diagnostic recording must be started before it can be flushed.");
            consumerObservation = _consumerObservation
                ?? throw new InvalidOperationException(
                    "Startup diagnostic consumer observation is unavailable.");
        }

        TaskCompletionSource flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_records.Writer.TryWrite(QueueItem.ForFlush(flush)))
        {
            return AwaitConsumerAsync(
                consumer,
                consumerObservation,
                timeout,
                cancellationToken);
        }

        return AwaitFlushAsync(
            flush.Task,
            consumer,
            timeout,
            cancellationToken);
    }

    /// <summary>Stops accepting records and waits within a caller-supplied bound for the consumer.</summary>
    public Task CompleteAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        Task? consumer;
        Task? consumerObservation;
        lock (_lifecycleLock)
        {
            if (!_completed)
            {
                _completed = true;
                _records.Writer.TryComplete();
            }

            consumer = _consumer;
            consumerObservation = _consumerObservation;
        }

        return consumer is null || consumerObservation is null
            ? Task.CompletedTask
            : AwaitConsumerAsync(
                consumer,
                consumerObservation,
                timeout,
                cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CompleteAsync(_disposeTimeout).ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (QueueItem item in _records.Reader.ReadAllAsync())
            {
                if (item.Diagnostic is not null)
                {
                    Persist(item.Diagnostic);
                    continue;
                }

                CompleteFlush(item.Flush!);
            }
        }
        finally
        {
            _records.Writer.TryComplete();
        }
    }

    private void Persist(PersistentDiagnostic diagnostic)
    {
        string? detail = diagnostic.Detail;
        if (diagnostic.Exception is not null)
        {
            string exceptionDetail = FormatExceptionDetail(diagnostic.Exception);
            detail = string.IsNullOrEmpty(detail)
                ? exceptionDetail
                : $"{detail}; {exceptionDetail}";
        }

        try
        {
            _appendLog(
                diagnostic.Level,
                diagnostic.Source,
                diagnostic.Message,
                detail);
        }
        catch (Exception primaryFailure) when (
            StartupCompletionFailurePolicy.IsRecoverable(primaryFailure))
        {
            try
            {
                _appendFallback(FormatFallbackLine(diagnostic, detail, primaryFailure));
            }
            catch (Exception fallbackFailure) when (
                StartupCompletionFailurePolicy.IsRecoverable(fallbackFailure))
            {
                _persistenceFailures.Add(primaryFailure);
                _persistenceFailures.Add(fallbackFailure);
            }
        }
    }

    /// <summary>Persists an exception thrown outside the ordered startup coordinator.</summary>
    public void RecordUnhandled(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Enqueue(new PersistentDiagnostic(
                "Error",
                LogSource,
                "Startup step 'application-launch' failed.",
                "order=-1; stage=Failed; outcome=; code=startup-unhandled-exception; elapsedMs=0.000",
                exception));
        }
        catch (Exception diagnosticFailure) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticFailure))
        {
            // Unhandled startup diagnostics are best effort even for hostile exception implementations.
        }
    }

    private static string FormatDetail(StartupDiagnosticRecord record)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "order={0}; stage={1}; outcome={2}; code={3}; elapsedMs={4:F3}; exceptionType={5}; exceptionMessage={6}",
            record.StepOrder,
            record.Stage,
            record.Outcome?.ToString() ?? string.Empty,
            record.DiagnosticCode ?? string.Empty,
            record.Elapsed.TotalMilliseconds,
            record.ExceptionType ?? string.Empty,
            record.ExceptionMessage ?? string.Empty);
    }

    private static string FormatDetailWithoutException(StartupDiagnosticRecord record)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "order={0}; stage={1}; outcome={2}; code={3}; elapsedMs={4:F3}",
            record.StepOrder,
            record.Stage,
            record.Outcome?.ToString() ?? string.Empty,
            record.DiagnosticCode ?? string.Empty,
            record.Elapsed.TotalMilliseconds);
    }

    private static string FormatExceptionDetail(Exception exception)
    {
        string typeName = exception.GetType().FullName
            ?? exception.GetType().Name;
        return string.Format(
            CultureInfo.InvariantCulture,
            "exceptionType={0}; exceptionMessage={1}",
            typeName,
            GetExceptionMessageSafely(exception) ?? string.Empty);
    }

    private async Task AwaitFlushAsync(
        Task flush,
        Task consumer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(flush, consumer)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        if (ReferenceEquals(completed, consumer))
        {
            ThrowPersistenceFailures();
        }
    }

    private async Task AwaitConsumerAsync(
        Task consumer,
        Task consumerObservation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await consumer.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        ThrowPersistenceFailures();
        await consumerObservation.ConfigureAwait(false);
    }

    private void CompleteFlush(TaskCompletionSource flush)
    {
        if (_persistenceFailures.Count == 0)
        {
            flush.TrySetResult();
            return;
        }

        flush.TrySetException(CreatePersistenceException());
    }

    private void ThrowPersistenceFailures()
    {
        if (_persistenceFailures.Count != 0)
        {
            throw CreatePersistenceException();
        }
    }

    private AggregateException CreatePersistenceException()
    {
        return new AggregateException(
            "One or more startup diagnostics could not be persisted.",
            _persistenceFailures);
    }

    private static string FormatFallbackLine(
        PersistentDiagnostic diagnostic,
        string? detail,
        Exception primaryFailure)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:O}\t{1}\t{2}\t{3}\t{4}\tprimaryWriterFailure={5}: {6}{7}",
            DateTimeOffset.UtcNow,
            diagnostic.Level,
            diagnostic.Source,
            diagnostic.Message,
            detail ?? string.Empty,
            primaryFailure.GetType().FullName,
            GetExceptionMessageSafely(primaryFailure) ?? string.Empty,
            Environment.NewLine);
    }

    private static string? GetExceptionMessageSafely(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch (Exception messageFailure) when (
            StartupCompletionFailurePolicy.IsRecoverable(messageFailure))
        {
            return null;
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Startup diagnostic waits require a finite positive timeout.");
        }
    }

    private static void AppendFallbackFile(string line)
    {
        string path = Path.Combine(
            AppDataPathService.ResolveLocalDataDirectory(),
            FallbackFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line);
    }

    private sealed class QueueItem
    {
        private QueueItem(PersistentDiagnostic? diagnostic, TaskCompletionSource? flush)
        {
            Diagnostic = diagnostic;
            Flush = flush;
        }

        public PersistentDiagnostic? Diagnostic { get; }

        public TaskCompletionSource? Flush { get; }

        public static QueueItem ForDiagnostic(PersistentDiagnostic diagnostic) => new(diagnostic, null);

        public static QueueItem ForFlush(TaskCompletionSource flush) => new(null, flush);
    }

    private sealed record PersistentDiagnostic(
        string Level,
        string Source,
        string Message,
        string? Detail,
        Exception? Exception);
}
