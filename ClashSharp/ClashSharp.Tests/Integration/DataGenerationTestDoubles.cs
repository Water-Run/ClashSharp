using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

internal sealed class BlockingDataGenerationFaultInjector(
    DataGenerationFaultPoint target,
    bool throwAfterRelease = false) : IDataGenerationFaultInjector
{
    private readonly TaskCompletionSource<object?> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public async Task InjectAsync(
        DataGenerationFaultPoint faultPoint,
        CancellationToken cancellationToken)
    {
        if (faultPoint != target)
        {
            return;
        }

        _entered.TrySetResult(null);
        await _release.Task.WaitAsync(cancellationToken);
        if (throwAfterRelease)
        {
            throw new IOException($"Injected fault after '{target}'.");
        }
    }

    public void Release()
    {
        _release.TrySetResult(null);
    }
}

internal sealed class FailOnceAsyncDisposable : IAsyncDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCount) == 1)
        {
            return ValueTask.FromException(
                new IOException("Injected transient scope disposal failure."));
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class CountingAsyncDisposable : IAsyncDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingAsyncDisposable : IAsyncDisposable
{
    private readonly TaskCompletionSource<object?> _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeCount;

    public Task Entered => _entered.Task;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        _entered.TrySetResult(null);
        await _release.Task;
    }

    public void Release()
    {
        _release.TrySetResult(null);
    }
}
