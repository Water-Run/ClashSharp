using System.Text;

namespace ClashSharp.Settings;

/// <summary>Parses, validates and formats the persisted Master Control layout template list.</summary>
/// <remarks>
/// Canonical text joins entries with <c>;</c>. A built-in reference is <c>@id</c>; a custom template is
/// <c>custom:</c> followed by the RFC 3986 escaped name, <c>:</c> and comma-separated tile identifiers.
/// Unknown built-in identifiers are preserved so a newer list survives a round trip through an older build.
/// </remarks>
public static class MasterInfoTilePresetList
{
    /// <summary>Maximum number of entries in the list.</summary>
    public const int MaximumEntries = 16;

    /// <summary>Maximum length of a custom template name after trimming.</summary>
    public const int MaximumNameLength = 40;

    /// <summary>Maximum number of tiles in one custom template.</summary>
    public const int MaximumTilesPerTemplate = 128;

    private const string CustomPrefix = "custom:";

    /// <summary>Gets the built-in templates offered by this build, in their default order.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = Array.AsReadOnly(["default", "extended", "minimal", "technical", "general", "complete"]);

    /// <summary>Gets the canonical text of the default template list.</summary>
    public static string DefaultCanonicalText { get; } = Format(BuiltInIds.Select(MasterInfoTilePresetEntry.BuiltIn));

    /// <summary>Parses canonical or user-supplied list text.</summary>
    /// <param name="text">Persisted list text; empty means an empty list.</param>
    /// <param name="entries">Normalized entries in their original order, without duplicates.</param>
    /// <param name="errorCode">Stable error suffix when parsing fails.</param>
    /// <returns>True when every entry is valid.</returns>
    public static bool TryParse(string text, out IReadOnlyList<MasterInfoTilePresetEntry> entries, out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<MasterInfoTilePresetEntry> parsed = [];
        HashSet<string> builtIns = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        entries = [];
        foreach (string token in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            MasterInfoTilePresetEntry entry;
            if (token.StartsWith('@'))
            {
                if (!TryNormalizeToken(token[1..], out string id))
                {
                    errorCode = "master_info_tile_preset.unsafe_id";
                    return false;
                }

                entry = MasterInfoTilePresetEntry.BuiltIn(id);
                if (!builtIns.Add(id)) { continue; }
            }
            else if (token.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                string[] parts = token[CustomPrefix.Length..].Split(':');
                string name;
                try
                {
                    name = parts.Length == 2 ? Uri.UnescapeDataString(parts[0]) : string.Empty;
                }
                catch (UriFormatException)
                {
                    name = string.Empty;
                }

                if (!TryNormalizeName(name, out string normalizedName)
                    || !TryNormalizeTileIds(parts[^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out IReadOnlyList<string> ids))
                {
                    errorCode = "master_info_tile_preset.invalid_custom";
                    return false;
                }

                entry = MasterInfoTilePresetEntry.Custom(normalizedName, ids);
                if (!names.Add(normalizedName)) { continue; }
            }
            else
            {
                errorCode = "master_info_tile_preset.unknown_entry";
                return false;
            }

            parsed.Add(entry);
            if (parsed.Count > MaximumEntries)
            {
                errorCode = "master_info_tile_preset.too_many";
                return false;
            }
        }

        entries = parsed.AsReadOnly();
        errorCode = null;
        return true;
    }

    /// <summary>Formats entries as canonical list text, keeping the first entry for each built-in or name.</summary>
    /// <param name="entries">Entries in display order.</param>
    /// <returns>Canonical list text.</returns>
    public static string Format(IEnumerable<MasterInfoTilePresetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        StringBuilder builder = new();
        HashSet<string> builtIns = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (MasterInfoTilePresetEntry entry in entries)
        {
            bool first = entry.IsBuiltIn ? builtIns.Add(entry.BuiltInId!) : names.Add(entry.Name!);
            if (!first) { continue; }
            if (builder.Length > 0) { builder.Append(';'); }
            if (entry.IsBuiltIn)
            {
                builder.Append('@').Append(entry.BuiltInId);
            }
            else
            {
                builder.Append(CustomPrefix).Append(Uri.EscapeDataString(entry.Name!)).Append(':').AppendJoin(',', entry.TileIds);
            }
        }

        return builder.ToString();
    }

    /// <summary>Normalizes a custom template name.</summary>
    /// <param name="name">Candidate name.</param>
    /// <param name="normalized">Trimmed name with internal whitespace collapsed to single spaces.</param>
    /// <returns>True when the name is 1 to <see cref="MaximumNameLength"/> characters without control characters.</returns>
    public static bool TryNormalizeName(string? name, out string normalized)
    {
        normalized = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length is > 0 and <= MaximumNameLength && !normalized.Any(char.IsControl);
    }

    /// <summary>Normalizes ordered tile identifiers, removing duplicates.</summary>
    /// <param name="tileIds">Candidate identifiers.</param>
    /// <param name="normalized">Lowercase identifiers in first-seen order.</param>
    /// <returns>True when 1 to <see cref="MaximumTilesPerTemplate"/> safe identifiers remain.</returns>
    public static bool TryNormalizeTileIds(IEnumerable<string> tileIds, out IReadOnlyList<string> normalized)
    {
        ArgumentNullException.ThrowIfNull(tileIds);
        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        normalized = [];
        foreach (string candidate in tileIds)
        {
            if (!TryNormalizeToken(candidate, out string id)) { return false; }
            if (seen.Add(id)) { ids.Add(id); }
        }

        if (ids.Count is 0 or > MaximumTilesPerTemplate) { return false; }
        normalized = ids.AsReadOnly();
        return true;
    }

    /// <summary>Normalizes one identifier token: ASCII letters, digits and hyphens, 1 to 64 characters, lowercase.</summary>
    internal static bool TryNormalizeToken(string? candidate, out string token)
    {
        token = (candidate ?? string.Empty).Trim().ToLowerInvariant();
        return token.Length is > 0 and <= 64 && token.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
    }
}
