/*
 * Notification Level
 * User-facing policy for Windows system notifications
 *
 * @author: WaterRun
 * @file: Model/NotificationLevel.cs
 * @date: 2026-06-26
 */

namespace ClashSharp.Model;

/// <summary>Controls how many Win11 system notifications Clash# sends.</summary>
public enum NotificationLevel
{
    Default,
    CriticalOnly,
    More,
}
