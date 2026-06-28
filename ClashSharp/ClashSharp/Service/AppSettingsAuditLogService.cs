/*
 * Application Settings Audit Log Service
 * Records persisted settings changes to the shared application log
 *
 * @author: WaterRun
 * @file: Service/AppSettingsAuditLogService.cs
 * @date: 2026-06-28
 */

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Service;

/// <summary>Subscribes to application setting changes and records an audit trail.</summary>
internal sealed class AppSettingsAuditLogService
{
    public static AppSettingsAuditLogService Instance { get; } = new(AppSettingsService.Instance, LogStorageService.Instance);

    private readonly AppSettingsService _settings;
    private readonly LogStorageService _logStorage;
    private int _isStarted;

    internal AppSettingsAuditLogService(AppSettingsService settings, LogStorageService logStorage)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    /// <summary>Starts auditing settings changes once for the process lifetime.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _isStarted, 1) == 1)
        {
            return;
        }

        _settings.SettingChanged += Settings_SettingChanged;
    }

    private void Settings_SettingChanged(object? sender, AppSettingChangedEventArgs e)
    {
        string message = e.WasRemoved
            ? $"Setting reset: {e.Key}"
            : $"Setting changed: {e.Key}";
        string detail = $"Previous: {FormatValue(e.OldValue)}{Environment.NewLine}New: {FormatValue(e.NewValue)}";

        try
        {
            _logStorage.AppendLog("Info", "Settings", message, detail);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
        {
            // Auditing must not prevent a user-requested settings write.
        }
    }

    private static string FormatValue(object? value)
    {
        return value is null
            ? "<default>"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
