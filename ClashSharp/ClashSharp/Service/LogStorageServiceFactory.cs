/*
 * Log Storage Service Factory
 * Wires production dependencies for SQLite log storage
 *
 * @author: WaterRun
 * @file: Service/LogStorageServiceFactory.cs
 * @date: 2026-06-25
 */

using System.IO;

namespace ClashSharp.Service;

public sealed partial class LogStorageService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="LogStorageService"/> instance.</value>
    public static LogStorageService Instance { get; } = LogStorageServiceFactory.CreateDefault();
}

/// <summary>Creates log storage services with production dependencies.</summary>
internal static class LogStorageServiceFactory
{
    /// <summary>Creates the default SQLite log storage service.</summary>
    public static LogStorageService CreateDefault()
    {
        return new LogStorageService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "ClashSharpLogs.sqlite3"),
            () => AppSettingsService.Instance.ActiveProfileId);
    }
}
