/*
 * Mainland China Text Display Service
 * Applies mainland China UI-only text replacement without mutating stored data
 *
 * @author: WaterRun
 * @file: Service/MainlandChinaTextDisplayService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Applies mainland China UI-only text replacement without mutating stored data.</summary>
/// <remarks>
/// Invariants: Replacement is applied only when the mainland China display policy is enabled.
/// Thread safety: Stateless service and safe for concurrent reads.
/// Side effects: Delegated to injected settings providers.
/// </remarks>
public sealed class MainlandChinaTextDisplayService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MainlandChinaTextDisplayService"/> instance.</value>
    public static MainlandChinaTextDisplayService Instance { get; } = MainlandChinaTextDisplayServiceFactory.CreateDefault();

    private readonly Func<MainlandChinaFeatureMode> _getFeatureMode;

    private readonly Func<bool> _getUrlBlockingEnabled;

    /// <summary>Region terms completed when mainland China text completion is enabled.</summary>
    private static readonly FrozenDictionary<string, string> TextCompletions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["香港"] = "中国香港",
        ["澳門"] = "中國澳門",
        ["澳门"] = "中国澳门",
        ["台灣"] = "中國台灣",
        ["台湾"] = "中国台湾",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Sensitive UI terms replaced when keyword filtering is enabled.</summary>
    private static readonly string[] SensitiveTerms =
    [
        "习近平",
        "習近平",
        "64",
        "六四",
        "天安门",
        "天安門",
        "品葱",
        "品蔥",
        "大纪元",
        "大紀元",
    ];

    /// <summary>Sensitive URL fragments masked when URL blacklist mode is enabled.</summary>
    private static readonly string[] SensitiveUrlFragments =
    [
        "pincong.rocks",
        "pincong.org",
        "pincong.icu",
        "epochtimes.com",
        "dajiyuan.com",
    ];

    /// <summary>Initializes the display service.</summary>
    /// <param name="getFeatureMode">Provider for the active mainland China display policy. Must not be null.</param>
    /// <param name="getUrlBlockingEnabled">Provider for the URL blocking preference. Must not be null.</param>
    internal MainlandChinaTextDisplayService(Func<MainlandChinaFeatureMode> getFeatureMode, Func<bool> getUrlBlockingEnabled)
    {
        _getFeatureMode = getFeatureMode ?? throw new ArgumentNullException(nameof(getFeatureMode));
        _getUrlBlockingEnabled = getUrlBlockingEnabled ?? throw new ArgumentNullException(nameof(getUrlBlockingEnabled));
    }

    /// <summary>Applies UI-only replacement to <paramref name="text"/> for the configured mainland China feature mode.</summary>
    /// <param name="text">Input display text. Must not be null.</param>
    /// <returns>Display text with sensitive terms replaced when the policy is enabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        MainlandChinaFeatureMode featureMode = _getFeatureMode();
        if (featureMode == MainlandChinaFeatureMode.Disabled)
        {
            return text;
        }

        if (_getUrlBlockingEnabled() && ContainsSensitiveUrl(text))
        {
            return "***";
        }

        string displayText = text;
        if (featureMode >= MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)
        {
            displayText = ApplyTextCompletions(displayText);
        }

        if (featureMode >= MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter)
        {
            foreach (string term in SensitiveTerms)
            {
                displayText = displayText.Replace(term, "***", StringComparison.OrdinalIgnoreCase);
            }
        }

        return displayText;
    }

    /// <summary>Determines whether text contains a blacklisted sensitive URL fragment.</summary>
    /// <param name="text">Display text to inspect. Must not be null.</param>
    /// <returns>True when a blacklisted URL fragment is present; otherwise false.</returns>
    private static bool ContainsSensitiveUrl(string text)
    {
        foreach (string fragment in SensitiveUrlFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Completes regional text without duplicating an existing China prefix.</summary>
    /// <param name="text">Display text to update. Must not be null.</param>
    /// <returns>Display text with regional names completed where needed.</returns>
    private static string ApplyTextCompletions(string text)
    {
        string displayText = text;
        foreach ((string term, string replacement) in TextCompletions)
        {
            displayText = displayText.Replace(replacement, term, StringComparison.Ordinal);
            displayText = displayText.Replace(term, replacement, StringComparison.Ordinal);
        }

        return displayText;
    }
}
