using System.Runtime.ExceptionServices;

namespace ClashSharp.Installer.Windows.Execution;

/// <summary>
/// The factory alone registers resources, then hands ownership to one session. Concurrent
/// disposal callers await the same drain; every release runs even if an earlier release fails.
/// </summary>
internal sealed class WindowsInstallerAuthorityScope : IAsyncDisposable
{
    private readonly List<Func<ValueTask>> _cleanup = [];
    private readonly Lazy<Task> _disposal;

    internal WindowsInstallerAuthorityScope() => _disposal = new(DisposeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    internal T Retain<T>(T resource) where T : IDisposable
    {
        _cleanup.Add(() =>
        {
            resource.Dispose();
            return ValueTask.CompletedTask;
        });
        return resource;
    }

    internal T RetainAsync<T>(T resource) where T : IAsyncDisposable
    {
        _cleanup.Add(resource.DisposeAsync);
        return resource;
    }

    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task DisposeCoreAsync()
    {
        List<Exception> failures = [];
        for (int index = _cleanup.Count - 1; index >= 0; index--)
        {
            try
            {
                await _cleanup[index]().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        _cleanup.Clear();
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }
}
