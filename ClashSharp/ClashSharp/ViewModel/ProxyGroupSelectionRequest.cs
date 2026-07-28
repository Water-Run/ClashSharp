using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Command payload for runtime proxy group selection.</summary>
/// <param name="Group">Runtime proxy group.</param>
/// <param name="ProxyName">Selected proxy name; never null.</param>
internal readonly record struct ProxyGroupSelectionRequest(MihomoProxyGroup Group, string ProxyName);
