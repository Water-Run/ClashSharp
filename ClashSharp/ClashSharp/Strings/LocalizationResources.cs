using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClashSharp.Model;

namespace ClashSharp.Strings;

/// <summary>Provides immutable localized string dictionaries for every supported display language.</summary>
/// <remarks>
/// Invariants: Every authored language catalog must contain the same key set as English.
/// Thread safety: Immutable dictionaries are safe for concurrent reads after type initialization.
/// Side effects: None.
/// </remarks>
internal static partial class LocalizationResources
{
    /// <summary>Gets immutable localized string dictionaries keyed by supported display language.</summary>
    /// <value>Two-level immutable lookup table; never null and never mutated after type initialization.</value>
    public static ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> Translations { get; } =
        BuildTranslations();

    private static ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> BuildTranslations()
    {
        Dictionary<AppLanguage, ReadOnlyDictionary<string, string>> table =
            new(BuildExplicitTranslations());

        FillMissingTranslations(table, AppLanguage.English);
        table[AppLanguage.AutoDetect] = table[AppLanguage.SimplifiedChinese];

        return new ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>>(table);
    }

    /// <summary>
    /// Builds only authored language catalogs, before the runtime English fallback is applied.
    /// </summary>
    /// <remarks>
    /// Localization completeness tests must inspect this boundary; validating <see cref="Translations"/>
    /// would only prove that fallback succeeded.
    /// </remarks>
    internal static ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>> BuildExplicitTranslations()
    {
        Dictionary<AppLanguage, ReadOnlyDictionary<string, string>> table = new()
        {
            [AppLanguage.SimplifiedChinese] = BuildSimplifiedChineseCatalog(),
            [AppLanguage.TraditionalChinese] = BuildTraditionalChineseCatalog(),
            [AppLanguage.English] = BuildEnglishCatalog(),
            [AppLanguage.Russian] = BuildRussianCatalog(),
            [AppLanguage.French] = BuildFrenchCatalog(),
            [AppLanguage.German] = BuildGermanCatalog(),
        };

        return new ReadOnlyDictionary<AppLanguage, ReadOnlyDictionary<string, string>>(table);
    }

    private static void FillMissingTranslations(
        Dictionary<AppLanguage, ReadOnlyDictionary<string, string>> table,
        AppLanguage fallbackLanguage)
    {
        IReadOnlyDictionary<string, string> fallback = table[fallbackLanguage];
        foreach ((AppLanguage language, ReadOnlyDictionary<string, string> translations) in table.ToArray())
        {
            Dictionary<string, string> completed = new(translations);
            foreach ((string key, string value) in fallback)
            {
                completed.TryAdd(key, value);
            }

            table[language] = new ReadOnlyDictionary<string, string>(completed);
        }
    }
}
