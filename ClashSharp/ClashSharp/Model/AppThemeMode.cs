/*
 * Application Theme Mode Enumeration
 * Defines user-selectable application display styles
 *
 * @author: WaterRun
 * @file: Model/AppThemeMode.cs
 * @date: 2026-06-17
 */

namespace ClashSharp.Model;

/// <summary>Enumerates application display style choices.</summary>
public enum AppThemeMode
{
    /// <summary>Follow the operating system app theme.</summary>
    FollowSystem = 0,

    /// <summary>Use light theme.</summary>
    Light = 1,

    /// <summary>Use dark theme.</summary>
    Dark = 2,
}
