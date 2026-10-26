using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;

namespace ClashSharp.Tests.Unit.Supervision;

public sealed class CompletedSupervisorFailureTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("quiesce")]
    [InlineData("resume")]
    public async Task CompletedFatalFailureRemainsObservableAndCannotRestartTheProducer(string operation)
    {
        var failure = new InvalidOperationException("Synthetic fatal wrapper.", Activator.CreateInstance<OutOfMemoryException>());
        int calls = 0;
        var loop = new SupervisedLoop("fatal-fixture", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException(failure);
        }, () => TimeSpan.FromHours(1), SystemSupervisorClock.Instance, new SupervisorBackoffPolicy(() => 0d));
        try
        {
            await loop.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (loop.IsRunning) { await Task.Delay(10, timeout.Token); }
            Assert.Equal(1, Volatile.Read(ref calls));

            Exception? actual = await Record.ExceptionAsync(() => operation switch
            {
                "start" => loop.StartAsync(CancellationToken.None),
                "quiesce" => loop.QuiesceAsync(CancellationToken.None),
                _ => loop.ResumeAsync(new QuiescedState(true), CancellationToken.None),
            });

            Assert.Same(failure, actual);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(loop.IsRunning);
            Assert.Same(failure, await Record.ExceptionAsync(() => loop.StopAsync(CancellationToken.None)));
        }
        finally
        {
            // Observe the intentionally faulted owned task even when the regression assertion fails.
            _ = await Record.ExceptionAsync(() => loop.StopAsync(CancellationToken.None));
        }
    }
}
