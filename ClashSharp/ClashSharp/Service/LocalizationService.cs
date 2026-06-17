/*
 * Centralized Localization Service
 * Provides localized UI strings for all supported languages with change-notification capability
 *
 * @author: WaterRun
 * @file: Service/LocalizationService.cs
 * @date: 2026-06-17
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ClashSharp.Strings;
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
    private static readonly ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> Translations = LocalizationResources.Translations;

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
}
