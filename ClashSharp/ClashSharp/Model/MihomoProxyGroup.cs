/*
 * Mihomo Proxy Group Model
 * Represents one selectable runtime proxy group exposed by mihomo
 *
 * @author: WaterRun
 * @file: Model/MihomoProxyGroup.cs
 * @date: 2026-06-24
 */

using System.Collections.Generic;
using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one selectable runtime proxy group exposed by mihomo.</summary>
/// <param name="Name">Proxy group name; never null.</param>
/// <param name="Type">Mihomo proxy group type; never null.</param>
/// <param name="CurrentSelection">Currently selected proxy name; never null.</param>
/// <param name="Candidates">Selectable proxy names; never null.</param>
public readonly record struct MihomoProxyGroup(
    string Name,
    string Type,
    string CurrentSelection,
    IReadOnlyList<string> Candidates)
{
    /// <summary>Gets UI-filtered proxy group name.</summary>
    /// <value>Display name after mainland China UI replacement.</value>
    public string NameDisplay => MainlandChinaTextDisplayService.Instance.Apply(Name);

    /// <summary>Gets UI-filtered current selection.</summary>
    /// <value>Current selection after mainland China UI replacement.</value>
    public string CurrentSelectionDisplay => MainlandChinaTextDisplayService.Instance.Apply(CurrentSelection);
}
