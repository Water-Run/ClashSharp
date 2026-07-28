using System;

namespace ClashSharp.Model;

/// <summary>Represents one proxy-provider or rule-provider resource exposed by mihomo.</summary>
/// <param name="Name">Provider name; never null.</param>
/// <param name="Kind">Provider namespace.</param>
/// <param name="VehicleType">Provider vehicle type such as HTTP or file; never null.</param>
/// <param name="Behavior">Rule provider behavior such as domain or ipcidr; never null.</param>
/// <param name="ItemCount">Provider item count.</param>
/// <param name="UpdatedAt">Last update time when mihomo reports it.</param>
public readonly record struct MihomoProviderResource(
    string Name,
    MihomoProviderKind Kind,
    string VehicleType,
    string Behavior,
    int ItemCount,
    DateTimeOffset? UpdatedAt);
