using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Provides the side-effect-free dependency-injection composition and owned service lifetime.</summary>
public sealed class AppHost : IApplicationHost
{
    private readonly object _syncLock = new();
    private readonly ServiceProvider _services;
    private Task? _stopTask;
    private Task? _disposeTask;
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
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("AppHost can only be started once.");
        }

        return _services.GetRequiredService<IApplicationStartupCoordinator>()
            .StartAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            _stopTask ??= _services.GetRequiredService<IApplicationShutdownCoordinator>()
                .StopAsync(cancellationToken);
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

    private sealed class NoOpApplicationShutdownCoordinator : IApplicationShutdownCoordinator
    {
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
