using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the shared asynchronous relay command.</summary>
public sealed class AsyncRelayCommandTests
{
    /// <summary>Verifies asynchronous execution invokes the supplied delegate.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCanExecuteIsTrue_InvokesDelegate()
    {
        int executionCount = 0;
        AsyncRelayCommand command = new(async cancellationToken =>
        {
            await Task.Yield();
            Assert.False(cancellationToken.IsCancellationRequested);
            executionCount++;
        }, new FakeApplicationErrorSink());

        await command.ExecuteAsync(null);

        Assert.Equal(1, executionCount);
    }

    /// <summary>Verifies reentrant execution is blocked while an operation is running.</summary>
    [Fact]
    public async Task ExecuteAsync_WhileRunning_BlocksReentrantExecution()
    {
        TaskCompletionSource started = new();
        TaskCompletionSource release = new();
        int executionCount = 0;
        AsyncRelayCommand command = new(async _ =>
        {
            executionCount++;
            started.SetResult();
            await release.Task;
        }, new FakeApplicationErrorSink());

        Task firstExecution = command.ExecuteAsync(null);
        await started.Task;

        Assert.False(command.CanExecute(null));

        Task secondExecution = command.ExecuteAsync(null);
        release.SetResult();
        await Task.WhenAll(firstExecution, secondExecution);

        Assert.Equal(1, executionCount);
        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies repeated ICommand invocation cannot replace the tracked in-flight task.</summary>
    [Fact]
    public async Task Execute_WhileRunning_PreservesTrackedExecutionAndBlocksReentrancy()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        AsyncRelayCommand command = new(async _ =>
        {
            executionCount++;
            started.TrySetResult();
            await release.Task;
        }, new FakeApplicationErrorSink());

        command.Execute(null);
        Task firstExecution = Assert.IsAssignableFrom<Task>(command.ExecutionTask);
        await started.Task;

        command.Execute(null);

        Assert.Same(firstExecution, command.ExecutionTask);
        Assert.Equal(1, executionCount);
        release.TrySetResult();
        await firstExecution;
        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies running-state changes raise command and property notifications.</summary>
    [Fact]
    public async Task ExecuteAsync_RaisesStateNotifications()
    {
        AsyncRelayCommand command = new(
            _ => Task.CompletedTask,
            new FakeApplicationErrorSink());
        int canExecuteNotifications = 0;
        List<string?> propertyNames = [];
        command.CanExecuteChanged += (_, _) => canExecuteNotifications++;
        command.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        await command.ExecuteAsync(null);

        Assert.Equal(2, canExecuteNotifications);
        Assert.Equal(["IsRunning", "IsBusy", "IsRunning", "IsBusy"], propertyNames);
    }

    /// <summary>Verifies ICommand execution observes an unexpected failure through one sink.</summary>
    [Fact]
    public async Task Execute_WhenDelegateFails_ReportsFailureAndTracksCompletion()
    {
        FakeApplicationErrorSink errorSink = new();
        InvalidOperationException failure = new("boom");
        AsyncRelayCommand command = new(
            _ => Task.FromException(failure),
            errorSink: errorSink,
            operationName: "test-operation");

        command.Execute(null);
        Task execution = Assert.IsAssignableFrom<Task>(command.ExecutionTask);
        await execution;

        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("test-operation", error.OperationName);
        Assert.Same(failure, error.Exception);
        Assert.Same(failure, command.LastError);
        Assert.False(command.IsBusy);
    }

    /// <summary>Verifies observed UI execution propagates caller cancellation and permits a later load.</summary>
    [Fact]
    public async Task ExecuteObservedAsync_WhenLifetimeIsCanceled_UsesCallerTokenAndAllowsNextInvocation()
    {
        using CancellationTokenSource firstLifetime = new();
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        List<CancellationToken> observedTokens = [];
        AsyncRelayCommand command = new(async cancellationToken =>
        {
            executionCount++;
            observedTokens.Add(cancellationToken);
            if (executionCount == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }, new FakeApplicationErrorSink());

        Task firstExecution = command.ExecuteObservedAsync(null, firstLifetime.Token);
        await firstStarted.Task;
        firstLifetime.Cancel();
        await firstExecution;
        await command.ExecuteObservedAsync(null, CancellationToken.None);

        Assert.Equal([firstLifetime.Token, CancellationToken.None], observedTokens);
        Assert.Equal(2, executionCount);
        Assert.False(command.IsBusy);
    }

    /// <summary>Verifies explicit execution preserves the caller token and resets busy state after cancellation.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_PropagatesOriginalCancellationAndResetsBusy()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<object?> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncRelayCommand command = new(async cancellationToken =>
        {
            started.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, new FakeApplicationErrorSink());

        Task execution = command.ExecuteAsync(null, cancellation.Token);
        await started.Task;
        Assert.True(command.IsBusy);
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(command.IsBusy);
        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies explicit failures reset busy state in the command's finally block.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenDelegateFails_ResetsBusyAndPropagatesFailure()
    {
        InvalidOperationException failure = new("failed");
        AsyncRelayCommand command = new(
            _ => Task.FromException(failure),
            new FakeApplicationErrorSink());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteAsync(null, CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.False(command.IsBusy);
        Assert.True(command.CanExecute(null));
    }

    /// <summary>Verifies cancellation is not silently contained when the caller token is still active.</summary>
    [Fact]
    public async Task Execute_WhenDelegateCancelsUnexpectedly_ReportsFailure()
    {
        FakeApplicationErrorSink errorSink = new();
        OperationCanceledException failure = new("unexpected cancellation");
        AsyncRelayCommand command = new(
            _ => Task.FromException(failure),
            errorSink,
            operationName: "unexpected-cancellation");

        command.Execute(null);
        await Assert.IsAssignableFrom<Task>(command.ExecutionTask);

        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Same(failure, error.Exception);
        Assert.Same(failure, command.LastError);
    }

    /// <summary>Verifies direct and wrapped process-fatal failures remain faulted for the owner to observe.</summary>
    [Theory]
    [MemberData(nameof(ProcessFatalCommandFailures))]
    public async Task Execute_WhenDelegateFailsFatally_PropagatesThroughExecutionTask(Exception failure)
    {
        FakeApplicationErrorSink errorSink = new();
        AsyncRelayCommand command = new(
            _ => Task.FromException(failure),
            errorSink,
            operationName: "fatal-command");

        command.Execute(null);
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(
            () => Assert.IsAssignableFrom<Task>(command.ExecutionTask));

        Assert.Same(failure, actual);
        Assert.Empty(errorSink.Errors);
        Assert.Null(command.LastError);
        Assert.False(command.IsBusy);
    }

    /// <summary>Verifies a wrapped fatal failure wins over an otherwise valid caller cancellation.</summary>
    [Fact]
    public async Task ExecuteObservedAsync_WhenCancellationWrapsFatalFailure_Propagates()
    {
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();
        OperationCanceledException failure = new(
            "cancelled while allocating",
            CreateProcessFatalException<OutOfMemoryException>(),
            callerCancellation.Token);
        AsyncRelayCommand command = new(
            _ => Task.FromException(failure),
            new FakeApplicationErrorSink());

        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => command.ExecuteObservedAsync(null, callerCancellation.Token));

        Assert.Same(failure, actual);
    }

    /// <summary>Verifies a process-fatal sink failure cannot be converted into command state.</summary>
    [Theory]
    [MemberData(nameof(ProcessFatalCommandFailures))]
    public async Task Execute_WhenErrorSinkFailsFatally_PropagatesSinkFailure(Exception sinkFailure)
    {
        InvalidOperationException commandFailure = new("ordinary command failure");
        ThrowingApplicationErrorSink errorSink = new(sinkFailure);
        AsyncRelayCommand command = new(
            _ => Task.FromException(commandFailure),
            errorSink,
            operationName: "sink-failure");

        command.Execute(null);
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(
            () => Assert.IsAssignableFrom<Task>(command.ExecutionTask));

        Assert.Same(sinkFailure, actual);
        Assert.Same(commandFailure, command.LastError);
    }

    /// <summary>Supplies representative direct and wrapped fatal graphs.</summary>
    public static TheoryData<Exception> ProcessFatalCommandFailures => new()
    {
        CreateProcessFatalException<OutOfMemoryException>(),
        new InvalidOperationException(
            "wrapper",
            CreateProcessFatalException<AccessViolationException>()),
        new AggregateException(
            new IOException("ordinary sibling"),
            CreateProcessFatalException<StackOverflowException>()),
    };

    private static TException CreateProcessFatalException<TException>()
        where TException : Exception =>
        Activator.CreateInstance<TException>();

    private sealed class FakeApplicationErrorSink : IApplicationErrorSink
    {
        public List<ApplicationError> Errors { get; } = [];

        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            Errors.Add(applicationError);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingApplicationErrorSink(Exception failure) : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(applicationError);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException(failure);
        }
    }
}
