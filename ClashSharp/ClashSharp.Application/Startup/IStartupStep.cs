namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Represents one explicitly ordered action in primary-instance startup.</summary>
public interface IStartupStep
{
    /// <summary>Gets the stable step name used for diagnostics and deterministic tie-breaking.</summary>
    string Name { get; }

    /// <summary>Gets the ascending execution order.</summary>
    int Order { get; }

    /// <summary>Executes the startup step.</summary>
    /// <param name="request">Current launch request.</param>
    /// <param name="cancellationToken">Cancels pre-side-effect startup work.</param>
    /// <returns>The typed step outcome.</returns>
    Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken);
}
