/*
 * Mainland China Feature Mode
 * Defines display-policy levels for mainland China specific UI behavior
 *
 * @author: WaterRun
 * @file: Model/MainlandChinaFeatureMode.cs
 * @date: 2026-06-17
 */

namespace ClashSharp.Model;

/// <summary>Display-policy levels for mainland China specific UI behavior.</summary>
public enum MainlandChinaFeatureMode
{
    /// <summary>No mainland China specific display behavior.</summary>
    Disabled = 0,

    /// <summary>Replace regional flag assets only.</summary>
    FlagReplacementOnly = 1,

    /// <summary>Replace regional flag assets and complete regional display names.</summary>
    FlagReplacementAndTextCompletion = 2,

    /// <summary>Replace flags, complete regional text, and filter sensitive keywords in UI text.</summary>
    FlagTextCompletionAndKeywordFilter = 3,

    /// <summary>Enable all mainland China display behavior, including blacklisted URL masking.</summary>
    AllIncludingUrlBlacklist = 4,
}
