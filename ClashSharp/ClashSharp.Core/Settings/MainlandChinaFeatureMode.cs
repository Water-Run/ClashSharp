namespace ClashSharp.Model;

/// <summary>Enumerates display-policy levels for mainland China specific UI behavior.</summary>
public enum MainlandChinaFeatureMode
{
    /// <summary>Use no mainland China specific display behavior.</summary>
    Disabled = 0,

    /// <summary>Replace regional flag assets only.</summary>
    FlagReplacementOnly = 1,

    /// <summary>Replace regional flag assets and complete regional display names.</summary>
    FlagReplacementAndTextCompletion = 2,

    /// <summary>Replace flags, complete regional text, and filter sensitive UI text.</summary>
    FlagTextCompletionAndKeywordFilter = 3,

    /// <summary>Legacy combined value that also enabled URL masking.</summary>
    AllIncludingUrlBlacklist = 4,
}
