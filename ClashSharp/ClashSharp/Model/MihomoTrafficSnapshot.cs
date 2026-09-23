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
    DateTimeOffset? CoreStartedAt = null);
