using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Contains detached controller groups observed under one verified active profile.</summary>
internal sealed record ProxyGroupSnapshot(string ProfileId, IReadOnlyList<MihomoProxyGroup> Groups);
