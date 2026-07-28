extern alias ClashSharpUi;

using HeadlessShutdownPolicy =
    ClashSharpUi::ClashSharp.Hosting.HeadlessShutdownPolicy;

namespace ClashSharp.Tests.Unit.Hosting;

/// <summary>Verifies bounded shutdown retry behavior for windowless application paths.</summary>
public sealed class HeadlessShutdownPolicyTests
{
    [Fact]
    public async Task TryCompleteAsync_FirstAttemptSucceeds_DoesNotRetry()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);
        int attempts = 0;

        bool completed = await policy.TryCompleteAsync(() =>
        {
            attempts++;
            return Task.FromResult(true);
        });

        Assert.True(completed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TryCompleteAsync_FirstAttemptFails_RetriesAndSucceeds()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);
        int attempts = 0;

        bool completed = await policy.TryCompleteAsync(() =>
        {
            attempts++;
            return Task.FromResult(attempts == 2);
        });

        Assert.True(completed);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task TryCompleteAsync_AllAttemptsFail_StopsAtBound()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);
        int attempts = 0;

        bool completed = await policy.TryCompleteAsync(() =>
        {
            attempts++;
            return Task.FromResult(false);
        });

        Assert.False(completed);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task TryCompleteAsync_RecoverableAttemptException_ReportsAndRetries()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);
        int attempts = 0;
        Exception? reportedException = null;

        bool completed = await policy.TryCompleteAsync(
            () =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<bool>(new IOException("shutdown boundary unavailable"))
                    : Task.FromResult(true);
            },
            exception => reportedException = exception);

        Assert.True(completed);
        Assert.Equal(2, attempts);
        Assert.IsType<IOException>(reportedException);
    }

    [Fact]
    public async Task TryCompleteAsync_CancellationException_DoesNotSwallowCancellation()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.TryCompleteAsync(
                () => Task.FromException<bool>(new OperationCanceledException())));
    }

    [Fact]
    public async Task TryCompleteAsync_WrappedProcessFatalException_DoesNotRetryOrReport()
    {
        HeadlessShutdownPolicy policy = new(maximumAttempts: 2);
        InvalidOperationException expected = new(
            "shutdown wrapper",
            CreateException<OutOfMemoryException>());
        int attempts = 0;
        int reports = 0;

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.TryCompleteAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException<bool>(expected);
                },
                _ => reports++));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
        Assert.Equal(0, reports);
    }

    private static TException CreateException<TException>()
        where TException : Exception, new() =>
        new();
}
