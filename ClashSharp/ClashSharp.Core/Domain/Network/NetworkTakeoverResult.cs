namespace ClashSharp.Model;

/// <summary>Identifies the process boundary that owns the effective mihomo runtime.</summary>
public enum MihomoCoreOwner
{
    /// <summary>No long-running mihomo process should exist.</summary>
    None,

    /// <summary>The desktop application owns a child mihomo process.</summary>
    App,

    /// <summary>The elevated Windows service owns the mihomo child process.</summary>
    Service,
}

/// <summary>Represents the verified outcome of applying a network takeover mode.</summary>
/// <param name="Mode">Takeover mode that was verified.</param>
/// <param name="CoreRunning">Whether the mihomo core is running.</param>
/// <param name="SystemProxyEnabled">Whether Windows system proxy is enabled.</param>
/// <param name="TransparentProxyEnabled">Whether TUN transparent proxy is active.</param>
/// <param name="Message">Localized human-readable outcome text.</param>
/// <param name="RequestedOwner">Runtime owner requested by the transition plan.</param>
/// <param name="TunRequested">Whether the transition requested TUN, even if it fell back.</param>
public readonly record struct NetworkTakeoverResult(
    ClashSharpMode Mode,
    bool CoreRunning,
    bool SystemProxyEnabled,
    bool TransparentProxyEnabled,
    string Message,
    MihomoCoreOwner RequestedOwner = MihomoCoreOwner.None,
    bool TunRequested = false)
{
    /// <summary>Gets the owner verified by the effective runtime flags.</summary>
    public MihomoCoreOwner EffectiveOwner => !CoreRunning
        ? MihomoCoreOwner.None
        : TransparentProxyEnabled
            ? MihomoCoreOwner.Service
            : MihomoCoreOwner.App;

    /// <summary>Gets whether TUN is effective, independently of the persisted preference.</summary>
    public bool TunEffective => TransparentProxyEnabled;
}
