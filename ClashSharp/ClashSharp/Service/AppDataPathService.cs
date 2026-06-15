/*
 * Application Data Path Service
 * Centralizes resolution of Clash# local application data paths
 *
 * @author: WaterRun
 * @file: Service/AppDataPathService.cs
 * @date: 2026-06-15
 */

using System;
using System.IO;
using Windows.Storage;

namespace ClashSharp.Service;

/// <summary>Resolves local application data paths used by Clash# services.</summary>
/// <remarks>
/// Invariants: Returned paths are absolute and suitable for local, user-scoped application data.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: None.
/// </remarks>
public static class AppDataPathService
{
    /// <summary>Fallback directory name used outside packaged Windows application context.</summary>
    private const string FallbackApplicationDirectoryName = "ClashSharp";

    /// <summary>Resolves the local application data directory.</summary>
    /// <returns>Absolute directory path for local application data; never null.</returns>
    public static string ResolveLocalDataDirectory()
    {
        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FallbackApplicationDirectoryName);
        }
    }
}
