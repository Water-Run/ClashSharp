/*
 * Windows Diagnostic Target Model
 * Defines Windows-native network diagnostic targets exposed by Clash#
 *
 * @author: WaterRun
 * @file: Model/WindowsDiagnosticTarget.cs
 * @date: 2026-06-15
 */

namespace ClashSharp.Model;

/// <summary>Defines Windows-native network diagnostic targets exposed by Clash#.</summary>
/// <remarks>
/// Invariants: Each value maps to one independent diagnostic and apply action.
/// Thread safety: Enum values are immutable and inherently thread-safe.
/// Side effects: None.
/// </remarks>
public enum WindowsDiagnosticTarget
{
    /// <summary>WSL proxy bridge through user environment variables and WSLENV.</summary>
    Wsl = 0,

    /// <summary>Terminal proxy environment variables for newly launched shells.</summary>
    Terminal = 1,

    /// <summary>Microsoft Store loopback exemption for local proxy access.</summary>
    MicrosoftStore = 2,
}
