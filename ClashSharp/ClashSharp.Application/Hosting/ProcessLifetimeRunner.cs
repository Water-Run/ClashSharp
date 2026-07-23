using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Lifecycle;

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

    /// <summary>Processes one accepted lifetime request after its producer releases all owned work.</summary>
    public Task ProcessAsync(
        ApplicationLifetimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            if (_host is null)
            {
                _stopTask = request.Handoff is null
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException(
                        "A durable lifetime handoff cannot be processed without an attached host."));
                return _stopTask;
            }

            IApplicationHost host = _host;
            _stopTask = ProcessAttachedRequestAsync(host, request, cancellationToken);
            return _stopTask;
        }
    }

    private async Task ProcessAttachedRequestAsync(
        IApplicationHost host,
        ApplicationLifetimeRequest request,
        CancellationToken cancellationToken)
    {
        IApplicationLifetimeHandoff? handoff = request.Handoff;
        if (handoff is not null)
        {
            await handoff.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            await handoff.MarkShutdownStartedAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception stopException)
        {
            if (handoff is not null)
            {
                (ApplicationLifetimeShutdownFailureKind failureKind, string diagnosticCode) =
                    ClassifyShutdownFailure(stopException);
                try
                {
                    await handoff.MarkShutdownFailedAsync(
                        failureKind,
                        diagnosticCode,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception handoffException)
                {
                    throw new AggregateException(stopException, handoffException);
                }
            }

            throw;
        }

        if (handoff is not null)
        {
            await handoff.MarkShutdownSucceededAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await host.DisposeAsync().ConfigureAwait(false);
        lock (_syncLock)
        {
            if (ReferenceEquals(_host, host))
            {
                _host = null;
            }
        }
    }

    private static (ApplicationLifetimeShutdownFailureKind Kind, string DiagnosticCode)
        ClassifyShutdownFailure(Exception exception)
    {
        return exception switch
        {
            RuntimeShutdownNotPreparedException notPrepared => (
                ApplicationLifetimeShutdownFailureKind.Failed,
                notPrepared.Result.ErrorCode ?? "trigger.handoff.shutdown_not_prepared"),
            OperationCanceledException => (
                ApplicationLifetimeShutdownFailureKind.Uncertain,
                "trigger.handoff.shutdown_cancelled"),
            _ => (
                ApplicationLifetimeShutdownFailureKind.Uncertain,
                "trigger.handoff.shutdown_unexpected"),
        };
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
