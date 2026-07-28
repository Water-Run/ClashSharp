using System.Collections.Generic;

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
    IReadOnlyList<string> Candidates);
