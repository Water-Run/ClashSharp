using System;

namespace ClashSharp.Model;

/// <summary>How one retained profile version reached durable state.</summary>
public enum ProfileHistoryApplyOutcome
{
    /// <summary>The validated version was archived without becoming active.</summary>
    Stored,

    /// <summary>The version was archived and successfully made active.</summary>
    Applied,

    /// <summary>The retained version was reapplied as an explicit rollback target.</summary>
    RollbackApplied,
}

/// <summary>Describes one immutable, locally archived profile configuration version.</summary>
/// <param name="VersionId">Stable version identifier; never null.</param>
/// <param name="ProfileId">Owning profile identifier; never null.</param>
/// <param name="CreatedAt">Time when the version was successfully imported.</param>
/// <param name="SourceName">Profile source display name at import time; never null.</param>
/// <param name="NodeCount">Validated proxy node count.</param>
/// <param name="RuleCount">Validated rule count.</param>
/// <param name="ContentSha256">Lowercase SHA-256 digest of the retained source bytes.</param>
/// <param name="ApplyOutcome">Durable activation outcome recorded for this version.</param>
/// <remarks>
/// Invariants: String values are never null and count values are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ProfileHistoryEntry(
    string VersionId,
    string ProfileId,
    DateTimeOffset CreatedAt,
    string SourceName,
    int NodeCount,
    int RuleCount,
    string ContentSha256,
    ProfileHistoryApplyOutcome ApplyOutcome);
