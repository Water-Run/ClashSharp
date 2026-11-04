namespace ClashSharp.Settings;

/// <summary>One entry of the Master Control layout template list: a built-in reference or a named custom layout.</summary>
/// <remarks>
/// Invariants: exactly one of <see cref="BuiltInId"/> and <see cref="Name"/> is set; custom entries carry at least one
/// tile identifier and built-in entries carry none, because built-in tile sets are resolved by the presentation layer.
/// </remarks>
public sealed class MasterInfoTilePresetEntry
{
    private MasterInfoTilePresetEntry(string? builtInId, string? name, IReadOnlyList<string> tileIds)
    {
        BuiltInId = builtInId;
        Name = name;
        TileIds = tileIds;
    }

    /// <summary>Gets the built-in template identifier, or <see langword="null"/> for a custom template.</summary>
    public string? BuiltInId { get; }

    /// <summary>Gets the user-chosen name of a custom template, or <see langword="null"/> for a built-in template.</summary>
    public string? Name { get; }

    /// <summary>Gets the ordered tile identifiers of a custom template; empty for a built-in reference.</summary>
    public IReadOnlyList<string> TileIds { get; }

    /// <summary>Gets whether this entry references a built-in template.</summary>
    public bool IsBuiltIn => BuiltInId is not null;

    /// <summary>Creates a reference to a built-in template.</summary>
    /// <param name="id">Lowercase identifier token, such as <c>daily</c>.</param>
    /// <returns>The built-in reference.</returns>
    /// <exception cref="ArgumentException">The identifier is not a safe token.</exception>
    public static MasterInfoTilePresetEntry BuiltIn(string id) =>
        MasterInfoTilePresetList.TryNormalizeToken(id, out string token)
            ? new(token, null, [])
            : throw new ArgumentException("The built-in template identifier is invalid.", nameof(id));

    /// <summary>Creates a named custom template.</summary>
    /// <param name="name">Display name; trimmed and limited to <see cref="MasterInfoTilePresetList.MaximumNameLength"/> characters.</param>
    /// <param name="tileIds">Ordered tile identifiers; duplicates are removed and at least one must remain.</param>
    /// <returns>The custom template.</returns>
    /// <exception cref="ArgumentException">The name or tile identifiers are invalid.</exception>
    public static MasterInfoTilePresetEntry Custom(string name, IEnumerable<string> tileIds)
    {
        ArgumentNullException.ThrowIfNull(tileIds);
        if (!MasterInfoTilePresetList.TryNormalizeName(name, out string normalizedName))
        {
            throw new ArgumentException("The template name is empty, too long or contains control characters.", nameof(name));
        }

        if (!MasterInfoTilePresetList.TryNormalizeTileIds(tileIds, out IReadOnlyList<string> ids))
        {
            throw new ArgumentException("The template must contain between 1 and 128 valid tile identifiers.", nameof(tileIds));
        }

        return new(null, normalizedName, ids);
    }
}
