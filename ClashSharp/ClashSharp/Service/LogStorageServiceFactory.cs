using System;
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
        return CreateForDirectory(
            AppDataPathService.ResolveLocalDataDirectory(),
            () => AppSettingsService.Instance.ActiveProfileId);
    }

    /// <summary>Creates one database service for an explicit data root without opening or creating files.</summary>
    public static LogStorageService CreateForDirectory(string dataDirectory, Func<string> getActiveProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The log data directory must be absolute.", nameof(dataDirectory));
        }

        return new LogStorageService(
            Path.Combine(Path.GetFullPath(dataDirectory), "ClashSharpLogs.sqlite3"),
            getActiveProfileId);
    }
}
