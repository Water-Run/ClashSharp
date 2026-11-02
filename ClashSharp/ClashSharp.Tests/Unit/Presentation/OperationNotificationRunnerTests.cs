extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class OperationNotificationRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedPortsReportOnceAndAnExplicitRetryStillReports(bool fails)
    {
        Recorder notifications = new();
        OperationNotificationRunner outer = new(notifications);
        OperationNotificationRunner inner = new(notifications);
        IOException failure = new("shared failure");
        for (int i = 0; i < 2; i++)
        {
            Task<bool> command = outer.ExecuteAsync(() => inner.ExecuteAsync(
                () => fails ? Task.FromException<bool>(failure) : Task.FromResult(true),
                "Inner", "Succeeded", "Failed", CancellationToken.None),
                "Outer", "Succeeded", "Failed", CancellationToken.None);
            if (fails) { await Assert.ThrowsAsync<IOException>(() => command); }
            else { Assert.True(await command); }
        }
        Assert.Equal(2, notifications.Messages.Count);
    }

    [Fact]
    public async Task SpecializedAvailabilityFailureDoesNotAlsoProduceAGenericOperationFailure()
    {
        Recorder notifications = new();
        IOException failure = new("verified network failure");
        await Assert.ThrowsAsync<IOException>(() => new OperationNotificationRunner(notifications).ExecuteAsync<bool>(() =>
        {
            ((IUserOperationNotifications)notifications).ReportAvailability("Runtime", "Unavailable", true);
            OperationNotificationRunner.MarkFailureReported(failure);
            return Task.FromException<bool>(failure);
        }, "Outer", "Succeeded", "Failed", CancellationToken.None));
        Assert.Equal(("Unavailable", true), Assert.Single(notifications.Messages));
        Assert.True(OperationNotificationRunner.HasReportedFailure(failure));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task OuterErrorBoundaryKeepsDiagnosticsAndOnlyNotifiesUncoveredFailures(int wrapping, int genericNotifications)
    {
        Recorder notifications = new();
        IOException original = new("owned operation failure");
        await Assert.ThrowsAsync<IOException>(() => new OperationNotificationRunner(notifications).ExecuteAsync(
            () => Task.FromException<bool>(original), "Title", "Success", "Failure", CancellationToken.None));
        Exception observed = wrapping switch
        {
            1 => new InvalidOperationException("outer boundary", original),
            2 => new AggregateException(original, new InvalidOperationException("wrapper", original)),
            3 => new AggregateException(original, new IOException("independent failure")),
            _ => original,
        };
        int logs = 0;
        int generic = 0;
        ClashSharpUi::ClashSharp.ApplicationErrorSink sink = new((_, _, _, _) => logs++, key => key, _ => generic++);
        await sink.ReportAsync(new ApplicationError("outer-command", observed), CancellationToken.None);
        Assert.Single(notifications.Messages);
        Assert.Equal(1, logs);
        Assert.Equal(genericNotifications, generic);
    }

    [Fact]
    public async Task MissingOperationNotificationBoundaryLeavesGenericErrorAvailable()
    {
        IOException failure = new("unreported");
        await Assert.ThrowsAsync<IOException>(() => new OperationNotificationRunner(null).ExecuteAsync(
            () => Task.FromException<bool>(failure), "Title", "Success", "Failure", CancellationToken.None));
        Assert.False(OperationNotificationRunner.HasReportedFailure(failure));
    }

    [Fact]
    public async Task ExplicitRetryReportsAgainWhenADependencyReusesItsException()
    {
        Recorder notifications = new();
        OperationNotificationRunner runner = new(notifications);
        IOException failure = new("retryable");
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(
                () => Task.FromException<bool>(failure), "Title", "Success", "Failure", CancellationToken.None));
        }
        Assert.Equal(2, notifications.Messages.Count);
    }

    [Fact]
    public async Task SuccessIsPublishedOnlyAfterTheOperationCommits()
    {
        Recorder notifications = new();
        OperationNotificationRunner runner = new(notifications);
        TaskCompletionSource<bool> commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> operation = runner.ExecuteAsync(() => commit.Task, "Title", "Succeeded", "Failed", CancellationToken.None);
        Assert.Empty(notifications.Messages);
        commit.SetResult(true);
        Assert.True(await operation);
        Assert.Equal(("Succeeded", false), Assert.Single(notifications.Messages));
    }

    [Theory]
    [InlineData(true, "Succeeded", false)]
    [InlineData(false, "Failed", true)]
    public async Task BooleanOutcomeDeterminesTheNotification(bool outcome, string message, bool failed)
    {
        Recorder notifications = new();
        await new OperationNotificationRunner(notifications).ExecuteAsync(() => Task.FromResult(outcome),
            "Title", "Succeeded", "Failed", CancellationToken.None);
        Assert.Equal((message, failed), Assert.Single(notifications.Messages));
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishFailure()
    {
        Recorder notifications = new();
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OperationNotificationRunner(notifications)
            .ExecuteAsync(() => Task.FromCanceled<bool>(source.Token), "Title", "Succeeded", "Failed", source.Token));
        Assert.Empty(notifications.Messages);
    }

    [Fact]
    public async Task TimeoutNotRequestedByCallerIsReportedAsFailure()
    {
        Recorder notifications = new();
        await Assert.ThrowsAsync<TimeoutException>(() => new OperationNotificationRunner(notifications)
            .ExecuteAsync(() => Task.FromException<bool>(new TimeoutException()), "Title", "Succeeded", "Failed", CancellationToken.None));
        Assert.Equal(("Failed", true), Assert.Single(notifications.Messages));
    }

    [Fact]
    public async Task SkippedBackgroundUpdateDoesNotPublishSuccess()
    {
        Recorder notifications = new();
        await new OperationNotificationRunner(notifications).ExecuteAsync(() => Task.FromResult<string?>(null),
            "Title", "Succeeded", "Failed", CancellationToken.None);
        Assert.Empty(notifications.Messages);
    }

    [Fact]
    public async Task NotificationFailureDoesNotReplaceTheCommittedResultOrOriginalError()
    {
        Recorder notifications = new() { FailDelivery = true };
        OperationNotificationRunner runner = new(notifications);
        Assert.Equal(7, await runner.ExecuteAsync(() => Task.FromResult(7), "Title", "Succeeded", "Failed", CancellationToken.None));
        IOException original = new("operation failed");
        Exception actual = await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(
            () => Task.FromException<int>(original), "Title", "Succeeded", "Failed", CancellationToken.None));
        Assert.Same(original, actual);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task ResultPolicyCanSuppressNoOpAndReportPartialFailure(int result, int expectedCount)
    {
        Recorder notifications = new();
        await new OperationNotificationRunner(notifications).ExecuteAsync(() => Task.FromResult(result),
            "Title", "Succeeded", "Failed", CancellationToken.None, count => count == 0 ? null : count == 1);
        Assert.Equal(expectedCount, notifications.Messages.Count);
        if (result == 2) { Assert.Equal(("Failed", true), notifications.Messages[0]); }
    }

    private sealed class Recorder : IUserOperationNotifications
    {
        public List<(string Message, bool Failed)> Messages { get; } = [];
        public bool FailDelivery { get; init; }
        public void ReportOperation(string titleResourceKey, string messageResourceKey, bool failed, params object[] arguments)
        {
            if (FailDelivery) { throw new InvalidOperationException("notification transport unavailable"); }
            Messages.Add((messageResourceKey, failed));
        }
    }
}
