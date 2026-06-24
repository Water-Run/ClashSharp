/*
 * Application Accent Color Mode Enumeration
 * Defines user-selectable application accent color behavior
 *
 * @author: WaterRun
 * @file: Model/AppAccentColorMode.cs
 * @date: 2026-06-24
 */

namespace ClashSharp.Model;

/// <summary>Enumerates application accent color choices.</summary>
public enum AppAccentColorMode
{
    /// <summary>Follow the operating system accent color.</summary>
    FollowSystem = 0,

    /// <summary>Use a user-selected custom accent color.</summary>
    Custom = 1,
}
