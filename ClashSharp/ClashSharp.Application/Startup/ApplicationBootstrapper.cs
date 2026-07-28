using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Hosting;

namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Enforces primary-instance arbitration before lazy host construction and startup.</summary>
public sealed class ApplicationBootstrapper
{
    private readonly IPrimaryInstanceBootstrap _primaryInstance;
    private readonly Func<IApplicationHost> _hostFactory;
    private readonly ProcessLifetimeRunner _lifetime;
    private readonly Func<AppLaunchRequest, CancellationToken, Task>? _onPrimaryOwned;

    /// <summary>Initializes the ownership-first launch orchestrator.</summary>
    /// <param name="primaryInstance">Minimal process-ownership boundary.</param>
    /// <param name="hostFactory">Lazy host factory invoked only by the primary process.</param>
    /// <param name="lifetime">App-owned outer host lifetime.</param>
    /// <param name="onPrimaryOwned">Optional callback awaited after arbitration and before host construction.</param>
    public ApplicationBootstrapper(
        IPrimaryInstanceBootstrap primaryInstance,
        Func<IApplicationHost> hostFactory,
        ProcessLifetimeRunner lifetime,
        Func<AppLaunchRequest, CancellationToken, Task>? onPrimaryOwned = null)
    {
        _primaryInstance = primaryInstance ?? throw new ArgumentNullException(nameof(primaryInstance));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _onPrimaryOwned = onPrimaryOwned;
    }

    /// <summary>Arbitrates ownership, then starts and attaches the primary host.</summary>
    /// <param name="request">Current activation request.</param>
    /// <param name="cancellationToken">Cancels arbitration and startup.</param>
    /// <returns>The process-level launch disposition.</returns>
    public async Task<ApplicationLaunchResult> LaunchAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PrimaryInstanceOwnership ownership = await _primaryInstance
            .AcquireAsync(request, cancellationToken);
        if (ownership == PrimaryInstanceOwnership.Redirected)
        {
            return new ApplicationLaunchResult(ApplicationLaunchDisposition.Redirected, null);
        }

        if (ownership != PrimaryInstanceOwnership.Primary)
        {
            throw new InvalidOperationException($"Unsupported primary-instance outcome '{ownership}'.");
        }

        if (_onPrimaryOwned is not null)
        {
            await _onPrimaryOwned(request, cancellationToken);
        }

        IApplicationHost host = _hostFactory()
            ?? throw new InvalidOperationException("The application host factory returned null.");
        StartupStepResult startupResult;
        try
        {
            startupResult = await host.StartAsync(request, cancellationToken);
            _lifetime.AttachHost(host);
        }
        catch (Exception startupException)
        {
            try
            {
                await ProcessLifetimeRunner.StopAndDisposeAsync(host, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(startupException, cleanupException);
            }

            ExceptionDispatchInfo.Capture(startupException).Throw();
            throw;
        }

        ApplicationLaunchDisposition disposition = startupResult.Outcome switch
        {
            StartupStepOutcome.Succeeded or StartupStepOutcome.Warning => ApplicationLaunchDisposition.Running,
            StartupStepOutcome.ExitRequested => ApplicationLaunchDisposition.ExitRequested,
            StartupStepOutcome.Fatal => ApplicationLaunchDisposition.Fatal,
            _ => throw new InvalidOperationException($"Unsupported startup outcome '{startupResult.Outcome}'."),
        };

        if (disposition == ApplicationLaunchDisposition.ExitRequested)
        {
            await _lifetime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new ApplicationLaunchResult(disposition, startupResult);
    }
}
