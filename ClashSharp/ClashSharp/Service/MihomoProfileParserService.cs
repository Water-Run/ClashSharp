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
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides imported text for the currently active profile.</summary>
internal interface IMihomoProfileTextSource
{
    /// <summary>Attempts to read the active imported profile configuration text.</summary>
    /// <returns>Configuration text when an imported active profile exists; otherwise null.</returns>
    string? TryReadActiveProfileText();
}

/// <summary>Extracts proxy node and rule preview rows from imported mihomo configuration files.</summary>
/// <remarks>
/// Invariants: Parsing is conservative and returns partial results instead of throwing on unsupported YAML shapes.
/// Thread safety: Stateless service and safe for concurrent reads.
/// Side effects: Requests active profile text from an injected source when active-profile parsing is used.
/// </remarks>
public sealed partial class MihomoProfileParserService
{
    private readonly IMihomoProfileTextSource _profileTextSource;

    private readonly Func<string, RegionMetadata> _resolveRegion;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the parser service.</summary>
    internal MihomoProfileParserService(
        IMihomoProfileTextSource profileTextSource,
        Func<string, RegionMetadata> resolveRegion,
        Func<string, string> getString)
    {
        _profileTextSource = profileTextSource ?? throw new ArgumentNullException(nameof(profileTextSource));
        _resolveRegion = resolveRegion ?? throw new ArgumentNullException(nameof(resolveRegion));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Parses proxy nodes from the active imported profile.</summary>
    /// <returns>Parsed proxy node rows; empty when no imported profile is active or parsing finds no nodes.</returns>
    public IReadOnlyList<ProxyNode> ParseActiveProfileNodes()
    {
        string? profileText = _profileTextSource.TryReadActiveProfileText();
        return string.IsNullOrWhiteSpace(profileText) ? [] : ParseNodes(profileText);
    }

    /// <summary>Parses rule preview rows from the active imported profile.</summary>
    /// <returns>Parsed rule preview rows; empty when no imported profile is active or parsing finds no rules.</returns>
    public IReadOnlyList<RulePreview> ParseActiveProfileRules()
    {
        string? profileText = _profileTextSource.TryReadActiveProfileText();
        return string.IsNullOrWhiteSpace(profileText) ? [] : ParseRules(profileText);
    }

    /// <summary>Parses proxy nodes from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <returns>Parsed proxy node rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    public IReadOnlyList<ProxyNode> ParseNodes(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        return MihomoProfilePreviewParser.ParseNodes(configurationText, _resolveRegion);
    }

    /// <summary>Parses rule preview rows from profile text.</summary>
    /// <param name="configurationText">mihomo configuration text. Must not be null.</param>
    /// <returns>Parsed rule preview rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    public IReadOnlyList<RulePreview> ParseRules(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        return MihomoProfilePreviewParser.ParseRules(configurationText, _getString);
    }
}
