using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Provides the side-effect-free dependency-injection composition and owned service lifetime.</summary>
public sealed class AppHost : IApplicationHost
{
    private readonly object _syncLock = new();
    private readonly ServiceProvider _services;
    private Task<StartupStepResult>? _startupTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private long _stopAttemptVersion;
    private int _started;

    private AppHost(ServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Builds a provider without resolving registered application services.</summary>
    /// <param name="configureServices">Adds application registrations without performing runtime work.</param>
    /// <returns>An unstarted application host.</returns>
    public static AppHost Build(Action<IServiceCollection> configureServices)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ServiceCollection services = new();
        services.TryAddSingleton<IApplicationShutdownCoordinator, NoOpApplicationShutdownCoordinator>();
        configureServices(services);
        return new AppHost(services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));
    }

    /// <inheritdoc />
    public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            ThrowIfDisposed();
            if (_stopAttemptVersion != 0 || Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("AppHost can only be started once and before stop is requested.");
            }

            _startupTask = StartCoreAsync(request, cancellationToken);
            return _startupTask;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            if (_stopTask is null)
            {
                long attemptVersion = ++_stopAttemptVersion;
                _stopTask = StopCoreAsync(attemptVersion, cancellationToken);
            }

            return _stopTask;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_syncLock)
        {
            _disposeTask ??= _services.DisposeAsync().AsTask();
            return new ValueTask(_disposeTask);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
    }

    private async Task<StartupStepResult> StartCoreAsync(AppLaunchRequest request, CancellationToken cancellationToken) =>
        await _services.GetRequiredService<IApplicationStartupCoordinator>()
            .StartAsync(request, cancellationToken).ConfigureAwait(false);

    private async Task StopCoreAsync(
        long attemptVersion,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            Task<StartupStepResult>? startup;
            lock (_syncLock) { startup = _startupTask; }
            if (startup is not null)
            {
                // The outer lifetime owns startup cancellation. Even after cancellation or a
                // failure, accepted startup work must settle before cleanup takes its snapshot.
                try { await startup.ConfigureAwait(false); }
                catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { }
            }
            await _services.GetRequiredService<IApplicationShutdownCoordinator>()
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_syncLock)
            {
                if (_stopAttemptVersion == attemptVersion)
                {
                    _stopTask = null;
                }
            }

            throw;
        }
    }

    private sealed class NoOpApplicationShutdownCoordinator : IApplicationShutdownCoordinator
    {
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
