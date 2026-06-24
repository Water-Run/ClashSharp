/*
 * Application Language Enumeration
 * Defines all display languages supported by the Clash# user interface
 *
 * @author: WaterRun
 * @file: Model/AppLanguage.cs
 * @date: 2026-04-08
 */

namespace ClashSharp.Model;

/// <summary>Enumerates all display languages supported by the application.</summary>
/// <remarks>
/// Invariants: Each member maps to a valid BCP-47 language tag.
/// Thread safety: Enum values are immutable and inherently thread-safe.
/// Side effects: None.
/// </remarks>
public enum AppLanguage
{
    /// <summary>Automatically follows the operating-system UI language when supported.</summary>
    AutoDetect = -1,

    /// <summary>Simplified Chinese (zh-Hans).</summary>
    SimplifiedChinese = 0,

    /// <summary>Traditional Chinese (zh-Hant).</summary>
    TraditionalChinese = 1,

    /// <summary>English (en-US).</summary>
    English = 2,

    /// <summary>Russian (ru).</summary>
    Russian = 3,

    /// <summary>French (fr).</summary>
    French = 4,

    /// <summary>German (de).</summary>
    German = 5,
}
