/*
 * Close Behavior Mode
 * Defines how the main window responds to user close requests
 *
 * @author: WaterRun
 * @file: Model/CloseBehaviorMode.cs
 * @date: 2026-06-26
 */

namespace ClashSharp.Model;

/// <summary>Close behavior for the main application window.</summary>
public enum CloseBehaviorMode
{
    ExitWithoutConfirmation,
    ConfirmExit,
    MinimizeToTray,
}
