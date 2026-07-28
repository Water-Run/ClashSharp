namespace ClashSharp.Service;

public sealed partial class ProxyRecoveryService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProxyRecoveryService"/> instance.</value>
    public static ProxyRecoveryService Instance { get; } = ProxyRecoveryServiceFactory.CreateDefault();
}

/// <summary>Creates stateless proxy recovery probes.</summary>
internal static class ProxyRecoveryServiceFactory
{
    /// <summary>Creates the default service used by application startup flows.</summary>
    /// <returns>A read-only proxy endpoint probe.</returns>
    public static ProxyRecoveryService CreateDefault()
    {
        return new ProxyRecoveryService();
    }
}
