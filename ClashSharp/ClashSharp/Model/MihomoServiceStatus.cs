/*
 * Mihomo Service Status Model
 * Represents Windows service deployment state used by transparent proxy settings
 *
 * @author: WaterRun
 * @file: Model/MihomoServiceStatus.cs
 * @date: 2026-06-24
 */

namespace ClashSharp.Model;

/// <summary>Represents the Windows service deployment state used by transparent proxy settings.</summary>
/// <param name="IsInstalled">True when the service exists.</param>
/// <param name="IsRunning">True when the service exists and reports running.</param>
/// <param name="Message">User-facing status message; never null.</param>
public readonly record struct MihomoServiceStatus(bool IsInstalled, bool IsRunning, string Message);
