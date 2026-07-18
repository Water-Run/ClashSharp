using System.Runtime.ExceptionServices;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Owns host stop and disposal outside the dependency-injection provider.</summary>
public sealed class ProcessLifetimeRunner
{
    private readonly object _syncLock = new();
    private IApplicationHost? _host;
    private Task? _stopTask;

    /// <summary>Gets whether a running host is currently attached.</summary>
    public bool HasAttachedHost
    {
        get
        {
            lock (_syncLock)
            {
                return _host is not null;
            }
        }
    }

    /// <summary>Attaches the one host owned by this process lifetime.</summary>
    /// <param name="host">Started primary application host.</param>
    public void AttachHost(IApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_syncLock)
        {
            if (_host is not null || _stopTask is not null)
            {
                throw new InvalidOperationException("A host is already attached or process shutdown has started.");
            }

            _host = host;
        }
    }

    /// <summary>Stops and disposes the attached host once.</summary>
    /// <param name="cancellationToken">Bounds host shutdown.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            if (_host is null)
            {
                _stopTask = Task.CompletedTask;
                return _stopTask;
            }

            IApplicationHost host = _host;
            _stopTask = StopAttachedAndDisposeAsync(host, cancellationToken);
            return _stopTask;
        }
    }

    private async Task StopAttachedAndDisposeAsync(
        IApplicationHost host,
        CancellationToken cancellationToken)
    {
        await host.StopAsync(cancellationToken).ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        lock (_syncLock)
        {
            if (ReferenceEquals(_host, host))
            {
                _host = null;
            }
        }
    }

    internal static async Task StopAndDisposeAsync(IApplicationHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        Exception? stopException = null;
        try
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopException = exception;
        }

        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException) when (stopException is not null)
        {
            throw new AggregateException(stopException, disposeException);
        }

        if (stopException is not null)
        {
            ExceptionDispatchInfo.Capture(stopException).Throw();
        }
    }
}
