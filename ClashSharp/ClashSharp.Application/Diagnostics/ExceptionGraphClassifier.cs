namespace ClashSharp.ApplicationModel.Diagnostics;

/// <summary>Describes how an application boundary must handle an exception graph.</summary>
public enum ExceptionGraphClassification
{
    /// <summary>The caller token is cancelled and the graph contains only cancellation failures.</summary>
    CallerCancellation,

    /// <summary>The graph contains cancellation that cannot be attributed solely to the caller.</summary>
    UnexpectedCancellation,

    /// <summary>The graph contains a failure after which the process must not continue normally.</summary>
    ProcessFatal,

    /// <summary>The graph contains only ordinary failures that a documented boundary may contain.</summary>
    Recoverable,
}

/// <summary>Classifies complete exception graphs without inspecting exception text.</summary>
/// <remarks>
/// Aggregate children and inner exceptions are traversed by reference. Cyclic graphs are safe.
/// A process-fatal node always wins. Caller cancellation is reported only when the supplied caller
/// token is cancelled and every terminal failure in the graph is a cancellation failure.
/// </remarks>
public static class ExceptionGraphClassifier
{
    /// <summary>Classifies an exception graph relative to one caller-owned cancellation token.</summary>
    public static ExceptionGraphClassification Classify(
        Exception exception,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Stack<Exception> pending = new();
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        bool containsCancellation = false;
        bool containsOrdinaryLeaf = false;
        pending.Push(exception);

        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (IsProcessFatalNode(current))
            {
                return ExceptionGraphClassification.ProcessFatal;
            }

            if (current is OperationCanceledException)
            {
                containsCancellation = true;
                if (current.InnerException is not null)
                {
                    pending.Push(current.InnerException);
                }

                continue;
            }

            if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Count == 0)
                {
                    containsOrdinaryLeaf = true;
                }
                else
                {
                    foreach (Exception innerException in aggregate.InnerExceptions)
                    {
                        pending.Push(innerException);
                    }
                }

                continue;
            }

            if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
                continue;
            }

            containsOrdinaryLeaf = true;
        }

        if (!containsCancellation)
        {
            return ExceptionGraphClassification.Recoverable;
        }

        return callerToken.IsCancellationRequested && !containsOrdinaryLeaf
            ? ExceptionGraphClassification.CallerCancellation
            : ExceptionGraphClassification.UnexpectedCancellation;
    }

    /// <summary>Returns whether the graph contains only ordinary recoverable failures.</summary>
    public static bool IsRecoverable(Exception exception) =>
        Classify(exception) == ExceptionGraphClassification.Recoverable;

    /// <summary>Returns whether the caller token is cancelled and the graph has only cancellation leaves.</summary>
    public static bool IsCallerCancellation(
        Exception exception,
        CancellationToken callerToken) =>
        Classify(exception, callerToken) == ExceptionGraphClassification.CallerCancellation;

    /// <summary>Returns whether the graph contains a direct or wrapped process-fatal failure.</summary>
    public static bool IsProcessFatal(Exception exception) =>
        Classify(exception) == ExceptionGraphClassification.ProcessFatal;

    private static bool IsProcessFatalNode(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}
