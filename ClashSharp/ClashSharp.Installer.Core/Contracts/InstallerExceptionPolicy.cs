namespace ClashSharp.Installer.Contracts;

/// <summary>Identifies severe failures that installer boundaries must never convert to ordinary results.</summary>
public static class InstallerExceptionPolicy
{
    /// <summary>Returns whether any direct, wrapped, or aggregate cause is process-fatal.</summary>
    /// <param name="exception">Failure graph to inspect without reading diagnostic text.</param>
    /// <returns>True when normal installer execution must not continue.</returns>
    /// <remarks>
    /// Every aggregate branch is inspected, including branches after ordinary failures. Shared
    /// and cyclic references are visited once; traversal does not recurse on the managed stack.
    /// Caller cancellation cannot override a severe cause contained in a cancellation wrapper.
    /// </remarks>
    public static bool IsProcessFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Stack<Exception> pending = new();
        HashSet<Exception> visited = new(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is OutOfMemoryException or StackOverflowException
                or AccessViolationException or AppDomainUnloadedException)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception child in aggregate.InnerExceptions)
                {
                    pending.Push(child);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }
}
