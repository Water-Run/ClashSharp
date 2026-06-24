/*
 * Mihomo Provider Resource Model
 * Represents one runtime provider resource exposed by mihomo
 *
 * @author: WaterRun
 * @file: Model/MihomoProviderResource.cs
 * @date: 2026-06-24
 */

using System;
using ClashSharp.Service;

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
    DateTimeOffset? UpdatedAt)
{
    /// <summary>Gets UI-filtered provider name.</summary>
    /// <value>Display name after mainland China UI replacement.</value>
    public string NameDisplay => MainlandChinaTextDisplayService.Instance.Apply(Name);

    /// <summary>Gets a concise provider type display.</summary>
    /// <value>Provider type display text.</value>
    public string TypeDisplay => Kind == MihomoProviderKind.Proxy ? "Proxy Provider" : "Rule Provider";

    /// <summary>Gets provider detail text for the UI.</summary>
    /// <value>Provider detail text.</value>
    public string DetailDisplay => Kind == MihomoProviderKind.Proxy
        ? string.IsNullOrWhiteSpace(VehicleType) ? "Proxy" : VehicleType
        : string.IsNullOrWhiteSpace(Behavior) ? "Rule" : Behavior;

    /// <summary>Gets a compact item-count display.</summary>
    /// <value>Item-count display text.</value>
    public string ItemCountDisplay => ItemCount.ToString("N0");

    /// <summary>Gets update time display text.</summary>
    /// <value>Update time display text.</value>
    public string UpdatedAtDisplay => UpdatedAt?.ToLocalTime().ToString("g") ?? "-";
}
