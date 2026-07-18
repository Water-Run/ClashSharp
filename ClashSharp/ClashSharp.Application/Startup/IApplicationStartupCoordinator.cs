namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Runs the ordered primary-instance startup pipeline.</summary>
public interface IApplicationStartupCoordinator
{
    /// <summary>Runs startup until completion, explicit exit, or fatal failure.</summary>
    /// <param name="request">Current launch request.</param>
    /// <param name="cancellationToken">Cancels startup before external work commits.</param>
    /// <returns>The aggregate startup outcome.</returns>
    Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken);
}
