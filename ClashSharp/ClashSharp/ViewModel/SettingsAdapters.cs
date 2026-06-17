/*
 * Settings Adapters
 * Connects settings view model helper delegates to application singleton services
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsAdapters.cs
 * @date: 2026-06-17
 */

#nullable enable

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Creates settings proxy information snapshots from runtime singleton services.</summary>
/// <remarks>
/// Invariants: Returned snapshots contain non-null paths.
/// Thread safety: Matches the wrapped singleton services and is intended for UI-thread use.
/// Side effects: Reads core configuration and core binary availability.
/// </remarks>
internal static class SettingsProxyInformationAdapter
{
    /// <summary>Creates the current proxy information snapshot.</summary>
    /// <returns>Current proxy information snapshot.</returns>
    public static SettingsProxyInformation CreateSnapshot()
    {
        CoreConfigurationState configurationState = CoreConfigurationService.Instance.GetState();
        return new SettingsProxyInformation(
            configurationState.ConfigPath,
            MihomoCoreService.Instance.IsBinaryAvailable,
            MihomoCoreService.Instance.BinaryPath);
    }
}
