namespace ClashSharp.Model;

/// <summary>Represents the verified outcome of applying a network takeover mode.</summary>
/// <param name="Mode">Takeover mode that was verified.</param>
/// <param name="CoreRunning">Whether the mihomo core is running.</param>
/// <param name="SystemProxyEnabled">Whether Windows system proxy is enabled.</param>
/// <param name="TransparentProxyEnabled">Whether TUN transparent proxy is active.</param>
/// <param name="Message">Localized human-readable outcome text.</param>
public readonly record struct NetworkTakeoverResult(
    ClashSharpMode Mode,
    bool CoreRunning,
    bool SystemProxyEnabled,
    bool TransparentProxyEnabled,
    string Message);
