using System.Runtime.CompilerServices;
using ClashSharp.Installer.Windows.Packages;
using Windows.Foundation;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsPackageOperationLifetimeTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("cancelled")]
    public async Task PendingOperationSurvivesCollectionUntilNativeTerminalCallback(string outcome)
    {
        var native = new NativeCompletion();
        Task<int> pending = Begin(native);
        try
        {
            await native.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Collect();

            Assert.True(native.IsProjectionAlive(), "The pending WinRT operation must remain owned independently of its Task.");
            Assert.False(pending.IsCompleted);
            native.Complete(outcome);

            if (outcome == "success")
            {
                Assert.Equal(41, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, native.GetResultsCount);
            }
            else if (outcome == "error")
            {
                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(native.Failure, error);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.Equal(0, native.CancelCount);
            Assert.Equal(0, native.CloseCount);
        }
        finally
        {
            // The fake native producer can finish an abandoned bridge even when a failing
            // assertion proves that the original managed projection has already disappeared.
            native.Drain(pending);
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<int> Begin(NativeCompletion native) =>
        WindowsPackageOperationLifetime.AwaitAsync(native.Create());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed class NativeCompletion
    {
        private WeakReference<ProjectedOperation>? _projection;
        private AsyncOperationWithProgressCompletedHandler<int, int>? _callback;
        internal TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("Synthetic deployment failure.");
        internal int GetResultsCount { get; set; }
        internal int CancelCount { get; set; }
        internal int CloseCount { get; set; }

        internal ProjectedOperation Create()
        {
            var projection = new ProjectedOperation(this);
            _projection = new(projection);
            return projection;
        }

        internal void Subscribe(AsyncOperationWithProgressCompletedHandler<int, int> callback)
        {
            // Like the native producer, this keeps the completion receiver alive, but does not
            // retain the caller's projected IAsyncOperation object on the managed heap.
            _callback = callback;
            Subscribed.SetResult();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool IsProjectionAlive() => _projection!.TryGetTarget(out _);

        internal void Complete(string outcome)
        {
            Assert.True(_projection!.TryGetTarget(out ProjectedOperation? projection));
            projection.Status = outcome switch
            {
                "success" => AsyncStatus.Completed,
                "error" => AsyncStatus.Error,
                "cancelled" => AsyncStatus.Canceled,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            _callback!(projection, projection.Status);
        }

        internal void Drain(Task pending)
        {
            if (!pending.IsCompleted && _callback is not null)
            {
                var terminal = new ProjectedOperation(this) { Status = AsyncStatus.Error };
                _callback(terminal, terminal.Status);
            }
        }
    }

    private sealed class ProjectedOperation(NativeCompletion native) : IAsyncOperationWithProgress<int, int>
    {
        private AsyncOperationWithProgressCompletedHandler<int, int>? _completed;
        public uint Id => 1;
        public AsyncStatus Status { get; internal set; } = AsyncStatus.Started;
        public Exception ErrorCode => native.Failure;
        public AsyncOperationProgressHandler<int, int> Progress { get; set; } = null!;
        public AsyncOperationWithProgressCompletedHandler<int, int> Completed
        {
            get => _completed!;
            set { _completed = value; native.Subscribe(value); }
        }

        public int GetResults()
        {
            Assert.Equal(AsyncStatus.Completed, Status);
            native.GetResultsCount++;
            return 41;
        }
        public void Cancel() => native.CancelCount++;
        public void Close() => native.CloseCount++;
    }
}
