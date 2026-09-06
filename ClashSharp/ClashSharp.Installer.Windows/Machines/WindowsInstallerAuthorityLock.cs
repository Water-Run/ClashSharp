using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsInstallerAuthorityLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

internal interface IWindowsInstallerAuthorityMutex : IDisposable
{
    bool TryAcquire();

    void Release();
}

/// <summary>
/// Holds one machine-wide mutex for an entire authenticated helper session. A dedicated owned
/// thread preserves native mutex thread affinity while the caller performs asynchronous work.
/// </summary>
internal sealed class WindowsInstallerAuthorityLock : IWindowsInstallerAuthorityLock
{
    private readonly Func<IWindowsInstallerAuthorityMutex> _openMutex;

    internal WindowsInstallerAuthorityLock()
        : this(WindowsInstallerAuthorityMutex.Open)
    {
    }

    internal WindowsInstallerAuthorityLock(Func<IWindowsInstallerAuthorityMutex> openMutex)
    {
        ArgumentNullException.ThrowIfNull(openMutex);
        _openMutex = openMutex;
    }

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return AuthorityLease.AcquireAsync(_openMutex, cancellationToken);
    }

    private sealed class AuthorityLease : IAsyncDisposable
    {
        private readonly TaskCompletionSource<IAsyncDisposable> _admitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private readonly Func<IWindowsInstallerAuthorityMutex> _openMutex;
        private readonly CancellationToken _cancellationToken;
        private int _releaseRequested;

        private AuthorityLease(
            Func<IWindowsInstallerAuthorityMutex> openMutex,
            CancellationToken cancellationToken)
        {
            _openMutex = openMutex;
            _cancellationToken = cancellationToken;
        }

        internal static async Task<IAsyncDisposable> AcquireAsync(
            Func<IWindowsInstallerAuthorityMutex> openMutex,
            CancellationToken cancellationToken)
        {
            var lease = new AuthorityLease(openMutex, cancellationToken);
            var thread = new Thread(lease.Run)
            {
                IsBackground = true,
                Name = "ClashSharp Installer authority lock",
            };
            try
            {
                thread.Start();
            }
            catch
            {
                lease._release.Dispose();
                throw;
            }

            try
            {
                // Even cancellation waits for the worker to finish opening and releasing the mutex.
                // After admission only lease disposal may release authority, never token cancellation.
                return await lease._admitted.Task.ConfigureAwait(false);
            }
            catch
            {
                await lease._stopped.Task.ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _releaseRequested, 1) == 0)
            {
                _release.Set();
            }

            await _stopped.Task.ConfigureAwait(false);
        }

        private void Run()
        {
            bool admitted = false;
            Exception? failure = null;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                using IWindowsInstallerAuthorityMutex mutex = _openMutex();
                if (!mutex.TryAcquire())
                {
                    throw new InstallerProtocolException("installer.concurrent_action_rejected");
                }

                try
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    admitted = _admitted.TrySetResult(this);
                    if (admitted)
                    {
                        _release.Wait();
                    }
                }
                finally
                {
                    mutex.Release();
                }
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                _admitted.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                failure = exception;
                _admitted.TrySetException(exception);
            }
            finally
            {
                _release.Dispose();
                if (admitted && failure is not null)
                {
                    _stopped.TrySetException(failure);
                }
                else
                {
                    _stopped.TrySetResult();
                }
            }
        }

        private static bool IsRecoverable(Exception exception) =>
            exception is not (OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or AppDomainUnloadedException);
    }
}
