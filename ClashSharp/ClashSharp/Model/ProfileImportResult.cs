/*
 * Profile Import Result Model
 * Represents the outcome of downloading, validating, and importing a subscription profile
 *
 * @author: WaterRun
 * @file: Model/ProfileImportResult.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Represents the outcome of downloading, validating, and importing a subscription profile.</summary>
/// <param name="ProfileId">Stable imported profile identifier; never null.</param>
/// <param name="ProfileName">Imported profile display name; never null.</param>
/// <param name="ConfigPath">Absolute imported profile configuration path; never null.</param>
/// <param name="NodeCount">Number of proxy nodes estimated from the imported profile.</param>
/// <param name="RuleCount">Number of rules estimated from the imported profile.</param>
/// <param name="Message">Human-readable import outcome; never null.</param>
/// <remarks>
/// Invariants: String values are never null; count values are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ProfileImportResult(
    string ProfileId,
    string ProfileName,
    string ConfigPath,
    int NodeCount,
    int RuleCount,
    string Message);
