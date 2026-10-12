namespace ClashSharp.Model;

/// <summary>Current parser output for one immutable archived profile version.</summary>
/// <param name="Entry">Original catalog entry retained unchanged for rollback authorization.</param>
/// <param name="NodeCount">Current node count, or null when the source could not be verified.</param>
/// <param name="RuleCount">Current rule count, or null when the source could not be verified.</param>
public readonly record struct ProfileHistoryPreview(ProfileHistoryEntry Entry, int? NodeCount, int? RuleCount)
{
    /// <summary>Gets whether the preview was derived from the verified retained source.</summary>
    public bool CanRestore => NodeCount is >= 0 && RuleCount is >= 0;
}
