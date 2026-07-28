namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Executes registered startup steps in deterministic order.</summary>
public sealed class StartupCoordinator : IApplicationStartupCoordinator
{
    private readonly IReadOnlyList<IStartupStep> _steps;
    private readonly IStartupDiagnosticSink? _diagnostics;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the coordinator from registered startup steps.</summary>
    /// <param name="steps">Startup steps to validate and order.</param>
    /// <param name="diagnostics">Optional best-effort diagnostic sink.</param>
    /// <param name="timeProvider">Clock used to measure step duration.</param>
    public StartupCoordinator(
        IEnumerable<IStartupStep> steps,
        IStartupDiagnosticSink? diagnostics = null,
        TimeProvider? timeProvider = null)
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
        _diagnostics = diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        StartupStepResult aggregate = StartupStepResult.Succeeded();

        foreach (IStartupStep step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? startedAt = GetTimestampSafely();
            RecordSafely(() => new StartupDiagnosticRecord(
                    step.Name,
                    step.Order,
                    StartupDiagnosticStage.Started,
                    null,
                    null,
                    TimeSpan.Zero,
                    null,
                    null));

            StartupStepResult result;
            try
            {
                result = await step.ExecuteAsync(request, cancellationToken);
            }
            catch (Exception exception)
            {
                RecordFailureSafely(() => new StartupDiagnosticRecord(
                        step.Name,
                        step.Order,
                        StartupDiagnosticStage.Failed,
                        null,
                        null,
                        GetElapsedTimeSafely(startedAt),
                        exception.GetType().FullName,
                        null),
                    exception);
                throw;
            }

            RecordSafely(() => new StartupDiagnosticRecord(
                    step.Name,
                    step.Order,
                    StartupDiagnosticStage.Completed,
                    result.Outcome,
                    result.DiagnosticCode,
                    GetElapsedTimeSafely(startedAt),
                    null,
                    null));
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

    private long? GetTimestampSafely()
    {
        try
        {
            return _timeProvider.GetTimestamp();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private TimeSpan GetElapsedTimeSafely(long? startedAt)
    {
        if (startedAt is null)
        {
            return TimeSpan.Zero;
        }

        try
        {
            return _timeProvider.GetElapsedTime(startedAt.Value);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return TimeSpan.Zero;
        }
    }

    private void RecordSafely(Func<StartupDiagnosticRecord> recordFactory)
    {
        try
        {
            if (_diagnostics is not null)
            {
                _diagnostics.Record(recordFactory());
            }
        }
        catch (Exception diagnosticFailure) when (IsRecoverable(diagnosticFailure))
        {
            // Startup diagnostics must never replace the startup result they describe.
        }
    }

    private void RecordFailureSafely(
        Func<StartupDiagnosticRecord> recordFactory,
        Exception exception)
    {
        try
        {
            if (_diagnostics is not null)
            {
                _diagnostics.RecordFailure(recordFactory(), exception);
            }
        }
        catch (Exception diagnosticFailure) when (!IsRecoverable(diagnosticFailure))
        {
            throw new AggregateException(exception, diagnosticFailure);
        }
        catch (Exception diagnosticFailure) when (IsRecoverable(diagnosticFailure))
        {
            // Startup diagnostics must never replace the startup failure they describe.
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        StartupCompletionFailurePolicy.IsRecoverable(exception);
}
