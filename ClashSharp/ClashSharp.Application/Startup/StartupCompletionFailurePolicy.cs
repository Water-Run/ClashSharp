using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Classifies failures that application boundaries may safely contain or retry.</summary>
public static class StartupCompletionFailurePolicy
{
    /// <summary>
    /// Returns whether an exception graph contains no cancellation or process-fatal failure.
    /// </summary>
    public static bool IsRecoverable(Exception exception)
    {
        return ExceptionGraphClassifier.IsRecoverable(exception);
    }
}
