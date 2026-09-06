using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerAuthorityLockTests
{
    [Fact]
    public async Task IndependentFactoriesExcludeEachOtherUntilAsynchronousDisposalFinishes()
    {
        string name = TemporaryName();
        var first = CreateLock(name);
        var second = CreateLock(name);
        IAsyncDisposable lease = await first.AcquireAsync(CancellationToken.None);
        try
        {
            InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
                () => second.AcquireAsync(CancellationToken.None));
            Assert.Equal("installer.concurrent_action_rejected", exception.DiagnosticCode);
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await using IAsyncDisposable successor = await second.AcquireAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationAfterAdmissionCannotReleaseAuthorityBeforeResourcesStop()
    {
        string name = TemporaryName();
        using var cancellation = new CancellationTokenSource();
        await using IAsyncDisposable lease = await CreateLock(name).AcquireAsync(cancellation.Token);

        cancellation.Cancel();

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => CreateLock(name).AcquireAsync(CancellationToken.None));
        Assert.Equal("installer.concurrent_action_rejected", exception.DiagnosticCode);
    }

    [Fact]
    public async Task PrecancelledAdmissionDoesNotOpenAKernelObject()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gate = new WindowsInstallerAuthorityLock(
            () => throw new InvalidOperationException("No object may be opened."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync(cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringOpenWaitsForWorkerAndReleasesAcquiredMutex()
    {
        using var cancellation = new CancellationTokenSource();
        using var finishOpen = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutex = new RecordingMutex();
        var gate = new WindowsInstallerAuthorityLock(() =>
        {
            opening.SetResult();
            Assert.True(finishOpen.Wait(TimeSpan.FromSeconds(10)));
            return mutex;
        });
        Task<IAsyncDisposable> admission = gate.AcquireAsync(cancellation.Token);
        try
        {
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(admission.IsCompleted);
        }
        finally
        {
            finishOpen.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
        Assert.Equal(1, mutex.Releases);
        Assert.True(mutex.Disposed);
        Assert.Equal(mutex.AcquiringThread, mutex.ReleasingThread);
    }

    [Fact]
    public async Task OpenFailureIsObservedAfterWorkerShutdown()
    {
        var expected = new IOException("injected");
        var gate = new WindowsInstallerAuthorityLock(() => throw expected);

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => gate.AcquireAsync(CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ReleaseFailureIsObservedAndTheNativeHandleIsDisposed()
    {
        var expected = new IOException("injected");
        var mutex = new RecordingMutex { ReleaseFailure = expected };
        var gate = new WindowsInstallerAuthorityLock(() => mutex);
        IAsyncDisposable lease = await gate.AcquireAsync(CancellationToken.None);

        IOException actual = await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());

        Assert.Same(expected, actual);
        Assert.True(mutex.Disposed);
        Assert.Equal(mutex.AcquiringThread, mutex.ReleasingThread);
    }

    [Fact]
    public async Task RepeatedDisposalReleasesTheMutexOnlyOnce()
    {
        var mutex = new RecordingMutex();
        var gate = new WindowsInstallerAuthorityLock(() => mutex);
        IAsyncDisposable lease = await gate.AcquireAsync(CancellationToken.None);

        await Task.WhenAll(lease.DisposeAsync().AsTask(), lease.DisposeAsync().AsTask());

        Assert.Equal(1, mutex.Releases);
        Assert.True(mutex.Disposed);
    }

    [Fact]
    public async Task AbandonedNativeMutexCanBeAcquiredForDurableRecovery()
    {
        string name = TemporaryName();
        using var previous = new TemporaryMutex(name);
        bool acquired = false;
        var thread = new Thread(() => acquired = previous.TryAcquire());
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(acquired);

        await using IAsyncDisposable recovered = await CreateLock(name).AcquireAsync(CancellationToken.None);
    }

    private static string TemporaryName() =>
        @"Local\ClashSharp.Installer.Tests." + Guid.NewGuid().ToString("N");

    private static WindowsInstallerAuthorityLock CreateLock(string name) =>
        new(() => new TemporaryMutex(name));

    // Exercise the production Win32 wait/release implementation with a unique user-owned object;
    // local tests never open the fixed administrator-only production mutex.
    private sealed class TemporaryMutex : IWindowsInstallerAuthorityMutex
    {
        private readonly Mutex _lifetime;
        private readonly WindowsInstallerAuthorityMutex _native;

        internal TemporaryMutex(string name)
        {
            _lifetime = new Mutex(initiallyOwned: false, name);
            _native = new WindowsInstallerAuthorityMutex(_lifetime.SafeWaitHandle);
        }

        public bool TryAcquire() => _native.TryAcquire();

        public void Release() => _native.Release();

        public void Dispose()
        {
            _native.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class RecordingMutex : IWindowsInstallerAuthorityMutex
    {
        internal Exception? ReleaseFailure { get; init; }
        internal int AcquiringThread { get; private set; }
        internal int ReleasingThread { get; private set; }
        internal int Releases { get; private set; }
        internal bool Disposed { get; private set; }

        public bool TryAcquire()
        {
            AcquiringThread = Environment.CurrentManagedThreadId;
            return true;
        }

        public void Release()
        {
            ReleasingThread = Environment.CurrentManagedThreadId;
            Releases++;
            if (ReleaseFailure is not null)
            {
                throw ReleaseFailure;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
