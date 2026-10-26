using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Observes completed producer failures before their owner can discard or replace the task.</summary>
internal static class RuntimeTaskFailurePolicy
{
    internal static void ThrowIfProcessFatal(Task? task)
    {
        if (task is null) { return; }
        if (!task.IsCompleted) { throw new InvalidOperationException("The owned runtime task has not completed."); }

        if (task.IsCanceled)
        {
            // Async methods mark an OperationCanceledException as cancellation even when its
            // inner graph is fatal. Task.Exception is null in that case; observe the await result.
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { }
        }
        else if (task.Exception is { } failure && ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            Exception original = failure.InnerExceptions.Count == 1 ? failure.InnerExceptions[0] : failure;
            ExceptionDispatchInfo.Capture(original).Throw();
        }
    }
}
