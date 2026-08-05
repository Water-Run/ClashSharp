namespace ClashSharp.Model;

/// <summary>Startup conflict categories shown in the startup check dialog.</summary>
internal enum StartupConflictKind
{
    /// <summary>An external mihomo process is already running.</summary>
    ExternalMihomoProcess,

    /// <summary>The configured mixed proxy port is occupied.</summary>
    MixedPortOccupied,

    /// <summary>Windows manual proxy is enabled but points to a different port.</summary>
    WindowsProxyWrongPort,

    /// <summary>A known third-party TUN or VPN interface is currently active.</summary>
    ActiveTunInterface,
}
