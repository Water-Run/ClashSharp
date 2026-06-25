/*
 * Data Package Export Scope
 * Describes user-facing backup/restore export operations
 *
 * @author: WaterRun
 * @file: Model/DataPackageExportScope.cs
 * @date: 2026-06-26
 */

namespace ClashSharp.Model;

/// <summary>Selectable data export scope shown in the backup and restore dialog.</summary>
internal enum DataPackageExportScope
{
    Settings,
    SettingsAndProxyConfiguration,
    SystemLogs,
    SystemLogSqlite,
}
