/*
 * Mihomo Profile Parser Service
 * Extracts proxy node and rule preview rows from imported mihomo configuration files
 *
 * @author: WaterRun
 * @file: Service/MihomoProfileParserService.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using System.IO;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Extracts proxy node and rule preview rows from imported mihomo configuration files.</summary>
/// <remarks>
/// Invariants: Parsing is conservative and returns partial results instead of throwing on unsupported YAML shapes.
/// Thread safety: Stateless service and safe for concurrent reads.
/// Side effects: Reads the active imported profile configuration file when it exists.
/// </remarks>
public sealed class MihomoProfileParserService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoProfileParserService"/> instance.</value>
    public static MihomoProfileParserService Instance { get; } = new();

    /// <summary>Initializes the parser service.</summary>
    private MihomoProfileParserService()
    {
    }

    /// <summary>Parses proxy nodes from the active imported profile.</summary>
    /// <returns>Parsed proxy node rows; empty when no imported profile is active or parsing finds no nodes.</returns>
    public IReadOnlyList<ProxyNode> ParseActiveProfileNodes()
    {
        string? profileText = TryReadActiveProfileText();
        return string.IsNullOrWhiteSpace(profileText) ? [] : ParseNodes(profileText);
    }

    /// <summary>Parses rule preview rows from the active imported profile.</summary>
    /// <returns>Parsed rule preview rows; empty when no imported profile is active or parsing finds no rules.</returns>
    public IReadOnlyList<RulePreview> ParseActiveProfileRules()
    {
        string? profileText = TryReadActiveProfileText();
        return string.IsNullOrWhiteSpace(profileText) ? [] : ParseRules(profileText);
    }

    /// <summary>Parses proxy nodes from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <returns>Parsed proxy node rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    public IReadOnlyList<ProxyNode> ParseNodes(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        return MihomoProfilePreviewParser.ParseNodes(configurationText, RegionDisplayService.Instance.Resolve);
    }

    /// <summary>Parses rule preview rows from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <returns>Parsed rule preview rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    public IReadOnlyList<RulePreview> ParseRules(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        return MihomoProfilePreviewParser.ParseRules(configurationText);
    }

    /// <summary>Attempts to read the active imported profile configuration text.</summary>
    /// <returns>Configuration text when an imported active profile exists; otherwise null.</returns>
    private static string? TryReadActiveProfileText()
    {
        string activeProfileId = AppSettingsService.Instance.ActiveProfileId;
        if (string.IsNullOrWhiteSpace(activeProfileId) || StringComparer.Ordinal.Equals(activeProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            return null;
        }

        string path = CoreConfigurationService.Instance.GetProfileConfigurationPath(activeProfileId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
