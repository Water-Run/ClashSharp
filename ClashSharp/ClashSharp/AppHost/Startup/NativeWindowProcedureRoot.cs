using System;
using System.Collections.Generic;

namespace ClashSharp.Hosting.Startup;

/// <summary>Roots delegates whose native window procedure could not be restored before process exit.</summary>
internal static class NativeWindowProcedureRoot
{
    private static readonly object SyncRoot = new();
    private static readonly List<object> ProcessLifetimeRoots = [];

    /// <summary>Retains a delegate and its target for the remainder of the process lifetime.</summary>
    internal static void Retain(object root)
    {
        ArgumentNullException.ThrowIfNull(root);

        lock (SyncRoot)
        {
            ProcessLifetimeRoots.Add(root);
        }
    }
}
