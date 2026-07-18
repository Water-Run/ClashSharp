namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Executes registered startup steps in deterministic order.</summary>
public sealed class StartupCoordinator : IApplicationStartupCoordinator
{
    private readonly IReadOnlyList<IStartupStep> _steps;

    /// <summary>Initializes the coordinator from registered startup steps.</summary>
    /// <param name="steps">Startup steps to validate and order.</param>
    public StartupCoordinator(IEnumerable<IStartupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        IStartupStep[] orderedSteps = steps
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Name, StringComparer.Ordinal)
            .ToArray();

        IGrouping<(int Order, string Name), IStartupStep>? duplicate = orderedSteps
            .GroupBy(static step => (step.Order, step.Name))
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Startup step '{duplicate.Key.Name}' has duplicate order {duplicate.Key.Order}.",
                nameof(steps));
        }

        _steps = orderedSteps;
    }

    /// <inheritdoc />
    public async Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        StartupStepResult aggregate = StartupStepResult.Succeeded();

        foreach (IStartupStep step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartupStepResult result = await step.ExecuteAsync(request, cancellationToken);
            switch (result.Outcome)
            {
                case StartupStepOutcome.Succeeded:
                    break;
                case StartupStepOutcome.Warning:
                    aggregate = result;
                    break;
                case StartupStepOutcome.ExitRequested:
                case StartupStepOutcome.Fatal:
                    return result;
                default:
                    throw new InvalidOperationException($"Unsupported startup outcome '{result.Outcome}'.");
            }
        }

        return aggregate;
    }
}
