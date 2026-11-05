using System;
using System.Collections.Generic;

namespace ClashSharp.Model;

/// <summary>Captures process-wide counters, including closed connections, from one core instance.</summary>
internal sealed record MihomoTrafficSnapshot(
    Guid Epoch,
    long UploadTotalBytes,
    long DownloadTotalBytes,
    IReadOnlyList<ActiveConnection> Connections,
    long? MemoryBytes = null,
    DateTimeOffset? CoreStartedAt = null,
    bool ConnectionsComplete = true,
    int? TotalConnectionCount = null,
    string? ProfileId = null,
    string? ConfigurationIdentity = null)
{
    public int ActiveConnectionCount => TotalConnectionCount ?? Connections.Count;
}

/// <summary>Signals a reachable controller whose connection details were intentionally omitted.</summary>
internal sealed class IncompleteConnectionSnapshotException(int totalCount) : System.IO.IOException("Connection details exceed the display limit.")
{
    public int TotalCount { get; } = totalCount;
}
