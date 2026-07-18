using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Owns application services after primary-instance arbitration.</summary>
public interface IApplicationHost : IAsyncDisposable
{
    /// <summary>Starts the ordered primary-instance pipeline.</summary>
    /// <param name="request">Current launch request.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The typed startup outcome.</returns>
    Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken);

    /// <summary>Stops host-owned work before provider disposal.</summary>
    /// <param name="cancellationToken">Bounds shutdown.</param>
    Task StopAsync(CancellationToken cancellationToken);
}
