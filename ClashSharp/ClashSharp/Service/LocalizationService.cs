/*
 * Centralized Localization Service
 * Provides localized UI strings for all supported languages with change-notification capability
 *
 * @author: WaterRun
 * @file: Service/LocalizationService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Singleton service that resolves localized string resources for all supported languages.</summary>
/// <remarks>
/// Invariants: <see cref="Instance"/> is non-null after type initialization. All language dictionaries contain identical key sets.
/// Thread safety: Not thread-safe; must be accessed from the UI thread only.
/// Side effects: Fires <see cref="LanguageChanged"/> when <see cref="CurrentLanguage"/> is modified.
/// </remarks>
public sealed class LocalizationService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="LocalizationService"/> instance.</value>
    public static LocalizationService Instance { get; } = new();

    /// <summary>Backing field for the currently active display language.</summary>
    private AppLanguage _currentLanguage = AppLanguage.SimplifiedChinese;

    /// <summary>Immutable lookup table mapping each language to its resource dictionary.</summary>
    private static readonly ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> Translations = BuildTranslations();

    /// <summary>Gets or sets the active display language for the application.</summary>
    /// <value>The currently selected <see cref="AppLanguage"/>. Defaults to <see cref="AppLanguage.SimplifiedChinese"/>.</value>
    public AppLanguage CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage == value)
            {
                return;
            }

            _currentLanguage = value;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Fires when <see cref="CurrentLanguage"/> transitions to a different value.</summary>
    /// <remarks>
    /// Fires synchronously on the thread that set <see cref="CurrentLanguage"/>.
    /// Reentrancy: Handlers must not set <see cref="CurrentLanguage"/> to avoid infinite recursion.
    /// </remarks>
    public event EventHandler? LanguageChanged;

    /// <summary>Initializes a new instance and selects the default language.</summary>
    private LocalizationService()
    {
    }

    /// <summary>Retrieves the localized string for <paramref name="key"/> in the current language.</summary>
    /// <param name="key">The resource key identifying the string. Must not be null.</param>
    /// <returns>The localized string if found; otherwise the key itself as a fallback.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Translations.TryGetValue(_currentLanguage, out ReadOnlyDictionary<string, string>? languageMap)
            && languageMap.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (Translations.TryGetValue(AppLanguage.English, out ReadOnlyDictionary<string, string>? fallbackMap)
            && fallbackMap.TryGetValue(key, out string? fallbackValue))
        {
            return fallbackValue;
        }

        return key;
    }

    /// <summary>Returns all supported languages paired with their native display names.</summary>
    /// <returns>An enumerable of tuples containing the <see cref="AppLanguage"/> value and its native name.</returns>
    public static IEnumerable<(AppLanguage Language, string DisplayName)> GetSupportedLanguages()
    {
        return
        [
            (AppLanguage.SimplifiedChinese, "简体中文"),
            (AppLanguage.TraditionalChinese, "繁體中文"),
            (AppLanguage.English, "English"),
            (AppLanguage.Russian, "Русский"),
            (AppLanguage.French, "Français"),
            (AppLanguage.German, "Deutsch"),
        ];
    }

    /// <summary>Builds the complete translation lookup table for every supported language.</summary>
    /// <returns>An immutable two-level dictionary mapping language then key to localized string.</returns>
    private static ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> BuildTranslations()
    {
        var table = new Dictionary<AppLanguage, ReadOnlyDictionary<string, string>>
        {
            [AppLanguage.SimplifiedChinese] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "主控",
                ["Nav.Proxies"] = "代理",
                ["Nav.ProxyNodes"] = "节点",
                ["Nav.Profiles"] = "配置",
                ["Nav.Connections"] = "连接",
                ["Nav.Rules"] = "规则",
                ["Nav.Statistics"] = "统计数据",
                ["Nav.Logs"] = "日志",
                ["Nav.Settings"] = "设置",
                ["Page.Stub"] = "开发中…",
                ["Page.MasterControl.Description"] = "控制 Clash# 的接管状态、核心运行和当前代理入口。",
                ["Page.ProxyNodes.Description"] = "查看节点、地区、延迟和当前策略组选择。",
                ["Page.Profiles.Description"] = "管理订阅、配置校验、自动更新和回滚。",
                ["Page.Connections.Description"] = "监视当前连接、进程、规则命中和代理路径。",
                ["Page.Rules.Description"] = "查看规则集、匹配结果和接管策略。",
                ["Page.Statistics.Description"] = "查看总量、配置、节点和时间维度的长期统计。",
                ["Page.Logs.Description"] = "查看 SQLite 日志、占用空间和清理入口。",
                ["Page.Settings.Description"] = "配置语言、透明代理、Windows 原生修复和中国大陆显示策略。",
            }),
            [AppLanguage.TraditionalChinese] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "主控",
                ["Nav.Proxies"] = "代理",
                ["Nav.ProxyNodes"] = "節點",
                ["Nav.Profiles"] = "設定檔",
                ["Nav.Connections"] = "連線",
                ["Nav.Rules"] = "規則",
                ["Nav.Statistics"] = "統計資料",
                ["Nav.Logs"] = "日誌",
                ["Nav.Settings"] = "設定",
                ["Page.Stub"] = "開發中…",
                ["Page.MasterControl.Description"] = "控制 Clash# 的接管狀態、核心執行和目前代理入口。",
                ["Page.ProxyNodes.Description"] = "檢視節點、地區、延遲和目前策略組選擇。",
                ["Page.Profiles.Description"] = "管理訂閱、設定檔校驗、自動更新和回復。",
                ["Page.Connections.Description"] = "監視目前連線、處理程序、規則命中和代理路徑。",
                ["Page.Rules.Description"] = "檢視規則集、匹配結果和接管策略。",
                ["Page.Statistics.Description"] = "檢視總量、設定檔、節點和時間維度的長期統計。",
                ["Page.Logs.Description"] = "檢視 SQLite 日誌、占用空間和清理入口。",
                ["Page.Settings.Description"] = "設定語言、透明代理、Windows 原生修復和中國大陸顯示策略。",
            }),
            [AppLanguage.English] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "Master Control",
                ["Nav.Proxies"] = "Proxies",
                ["Nav.ProxyNodes"] = "Nodes",
                ["Nav.Profiles"] = "Profiles",
                ["Nav.Connections"] = "Connections",
                ["Nav.Rules"] = "Rules",
                ["Nav.Statistics"] = "Statistics",
                ["Nav.Logs"] = "Logs",
                ["Nav.Settings"] = "Settings",
                ["Page.Stub"] = "Under development…",
                ["Page.MasterControl.Description"] = "Control Clash# takeover state, core runtime, and current proxy entry.",
                ["Page.ProxyNodes.Description"] = "Review nodes, regions, latency, and active proxy group choices.",
                ["Page.Profiles.Description"] = "Manage subscriptions, configuration validation, automatic updates, and rollback.",
                ["Page.Connections.Description"] = "Monitor active connections, processes, rule hits, and proxy routes.",
                ["Page.Rules.Description"] = "Inspect rule providers, match results, and takeover policy.",
                ["Page.Statistics.Description"] = "Review long-term totals by profile, node, and time range.",
                ["Page.Logs.Description"] = "Review SQLite logs, storage usage, and cleanup actions.",
                ["Page.Settings.Description"] = "Configure language, transparent proxy, native Windows repair, and mainland China display policy.",
            }),
            [AppLanguage.Russian] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "Управление",
                ["Nav.Proxies"] = "Прокси",
                ["Nav.Profiles"] = "Профили",
                ["Nav.Connections"] = "Соединения",
                ["Nav.Statistics"] = "Статистика",
                ["Nav.Settings"] = "Настройки",
                ["Page.Stub"] = "В разработке…",
            }),
            [AppLanguage.French] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "Contrôle",
                ["Nav.Proxies"] = "Proxies",
                ["Nav.Profiles"] = "Profils",
                ["Nav.Connections"] = "Connexions",
                ["Nav.Statistics"] = "Statistiques",
                ["Nav.Settings"] = "Paramètres",
                ["Page.Stub"] = "En développement…",
            }),
            [AppLanguage.German] = new(new Dictionary<string, string>
            {
                ["Nav.MasterControl"] = "Steuerung",
                ["Nav.Proxies"] = "Proxies",
                ["Nav.Profiles"] = "Profile",
                ["Nav.Connections"] = "Verbindungen",
                ["Nav.Statistics"] = "Statistiken",
                ["Nav.Settings"] = "Einstellungen",
                ["Page.Stub"] = "In Entwicklung…",
            }),
        };

        return new ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>>(table);
    }
}
