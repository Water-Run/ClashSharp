using System.Net.Http;
using System.Text.Json;

namespace ClashSharp.ApplicationModel.Supervision;

/// <summary>Describes the externally observable state of a supervised loop.</summary>
public enum SupervisorHealthState
{
    /// <summary>The loop is intentionally disabled, quiesced, or stopped.</summary>
    Stopped,

    /// <summary>The loop is running without an unresolved failure.</summary>
    Healthy,

    /// <summary>The loop has a transient failure and is waiting to retry.</summary>
    Retrying,

    /// <summary>The loop has completed one successful recovery probe.</summary>
    Recovering,

    /// <summary>The loop has a persistent failure but continues bounded probes.</summary>
    Degraded,
}

/// <summary>Provides an immutable diagnostic snapshot for a supervised loop.</summary>
/// <param name="State">Current health state.</param>
/// <param name="ConsecutiveFailureCount">Number of consecutive failed iterations.</param>
/// <param name="ConsecutiveSuccessCount">Number of consecutive successful iterations.</param>
/// <param name="FirstFailureAt">Time of the first failure in the current failure streak.</param>
/// <param name="LastFailureAt">Time of the most recent failure.</param>
/// <param name="NextAttemptAt">Scheduled time of the next iteration, or null while an iteration is active.</param>
/// <param name="ErrorCode">Stable category for the most recent unresolved failure.</param>
/// <param name="LastSuccessAt">Time of the most recent successful iteration.</param>
public sealed record SupervisorHealth(
    SupervisorHealthState State,
    int ConsecutiveFailureCount,
    int ConsecutiveSuccessCount,
    DateTimeOffset? FirstFailureAt,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? NextAttemptAt,
    string? ErrorCode,
    DateTimeOffset? LastSuccessAt)
{
    /// <summary>Gets an initial intentionally stopped snapshot.</summary>
    public static SupervisorHealth Stopped { get; } = new(
        SupervisorHealthState.Stopped,
        0,
        0,
        null,
        null,
        null,
        null,
        null);
}

/// <summary>Maps operational failures to stable diagnostic categories without leaking exception text.</summary>
public static class SupervisorFailureClassifier
{
    /// <summary>Returns the stable code for an exception observed by a supervisor.</summary>
    /// <param name="exception">The caught iteration exception.</param>
    /// <returns>A stable category code suitable for health and telemetry.</returns>
    public static string Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsSqliteException(exception))
        {
            return "supervisor.sqlite";
        }

        return exception switch
        {
            IOException => "supervisor.io",
            HttpRequestException => "supervisor.http",
            JsonException => "supervisor.json",
            _ => "supervisor.unexpected",
        };
    }

    private static bool IsSqliteException(Exception exception)
    {
        for (Type? type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
