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
