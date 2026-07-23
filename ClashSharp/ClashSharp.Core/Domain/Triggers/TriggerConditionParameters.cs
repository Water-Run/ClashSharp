namespace ClashSharp.Model.Triggers;

/// <summary>Base type for immutable typed trigger-condition parameters.</summary>
public abstract record TriggerConditionParameters;

/// <summary>Parameters for an application or proxy event condition.</summary>
/// <param name="EventKind">Exact event that must be observed.</param>
public sealed record EventConditionParameters(TriggerEventKind EventKind) : TriggerConditionParameters;

/// <summary>Parameters for a notification event condition.</summary>
/// <param name="MinimumLevel">Minimum notification severity that matches.</param>
public sealed record NotificationConditionParameters(TriggerNotificationLevel MinimumLevel) : TriggerConditionParameters;

/// <summary>Parameters for a scoped traffic-byte threshold.</summary>
/// <param name="Scope">Traffic history scope.</param>
/// <param name="ThresholdBytes">Positive byte threshold.</param>
/// <param name="Window">Positive rolling duration only when <paramref name="Scope"/> is rolling-window.</param>
public sealed record TrafficConditionParameters(
    TriggerTrafficScope Scope,
    long ThresholdBytes,
    TimeSpan? Window = null) : TriggerConditionParameters;

/// <summary>Parameters for a traffic-rate threshold.</summary>
/// <param name="Direction">Rate direction.</param>
/// <param name="ThresholdBytesPerSecond">Positive byte-per-second threshold.</param>
public sealed record RateConditionParameters(
    TriggerTrafficDirection Direction,
    long ThresholdBytesPerSecond) : TriggerConditionParameters;

/// <summary>Parameters for an active-connection threshold.</summary>
/// <param name="Threshold">Positive connection-count threshold.</param>
public sealed record ActiveConnectionsConditionParameters(int Threshold) : TriggerConditionParameters;

/// <summary>Parameters for an application-runtime threshold.</summary>
/// <param name="Threshold">Positive runtime duration.</param>
public sealed record RuntimeConditionParameters(TimeSpan Threshold) : TriggerConditionParameters;

/// <summary>Parameters for a local time-of-day schedule.</summary>
/// <param name="TargetTime">Local time at or after which the condition matches.</param>
public sealed record SystemTimeConditionParameters(TimeOnly TargetTime) : TriggerConditionParameters;
