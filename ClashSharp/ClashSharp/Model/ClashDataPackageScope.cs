/*
 * Clash Data Package Scope
 * Describes export and import package coverage
 *
 * @author: WaterRun
 * @file: Model/ClashDataPackageScope.cs
 * @date: 2026-06-25
 */

namespace ClashSharp.Model;

/// <summary>Coverage level for Clash# XML data packages.</summary>
public enum ClashDataPackageScope
{
    Settings = 0,
    SettingsAndProxyConfiguration = 1,
    AllIncludingLogs = 2,
}
