using System.ComponentModel;
using System.Globalization;

namespace ClashSharp.Installer.Presentation;

/// <summary>The interface languages of the installer, the same eight the application offers.</summary>
public enum InstallerLanguage
{
    /// <summary>Simplified Chinese, the source language of every installer text.</summary>
    SimplifiedChinese = 0,

    /// <summary>Traditional Chinese.</summary>
    TraditionalChinese = 1,

    /// <summary>English.</summary>
    English = 2,

    /// <summary>Russian.</summary>
    Russian = 3,

    /// <summary>French.</summary>
    French = 4,

    /// <summary>German.</summary>
    German = 5,

    /// <summary>Persian, laid out right to left.</summary>
    Persian = 6,

    /// <summary>Korean.</summary>
    Korean = 7,
}

/// <summary>
/// Localized installer text. Every text is written once in Simplified Chinese, which is also its lookup key, and
/// the translation table holds the other seven languages next to it, so a text and its translations are always
/// reviewed and changed together.
/// </summary>
/// <remarks>
/// The language follows the Windows display language unless the user picks another one in the installer.
/// Changing it raises <see cref="PropertyChanged"/> for <see cref="Revision"/>, which window bindings observe.
/// A text missing from the table falls back to the Chinese source rather than failing.
/// </remarks>
public sealed class InstallerText : INotifyPropertyChanged
{
    private static readonly (InstallerLanguage Language, string NativeName, string Culture)[] Catalog =
    [
        (InstallerLanguage.SimplifiedChinese, "简体中文", "zh-Hans"),
        (InstallerLanguage.TraditionalChinese, "繁體中文", "zh-Hant"),
        (InstallerLanguage.English, "English", "en"),
        (InstallerLanguage.Russian, "Русский", "ru"),
        (InstallerLanguage.French, "Français", "fr"),
        (InstallerLanguage.German, "Deutsch", "de"),
        (InstallerLanguage.Persian, "فارسی", "fa"),
        (InstallerLanguage.Korean, "한국어", "ko"),
    ];

    private int _language;
    private int _revision;

    /// <summary>Creates text in the supplied language.</summary>
    /// <param name="language">Initial language; Simplified Chinese when omitted.</param>
    public InstallerText(InstallerLanguage language = InstallerLanguage.SimplifiedChinese)
    {
        ValidateLanguage(language);
        _language = (int)language;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the languages in display order with their native names.</summary>
    public static IReadOnlyList<string> LanguageNames { get; } = Array.AsReadOnly(Catalog.Select(entry => entry.NativeName).ToArray());

    /// <summary>Gets or sets the current language.</summary>
    public InstallerLanguage Language
    {
        get => (InstallerLanguage)Volatile.Read(ref _language);
        set
        {
            ValidateLanguage(value);
            if (Interlocked.Exchange(ref _language, (int)value) == (int)value) { return; }
            Interlocked.Increment(ref _revision);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        }
    }

    /// <summary>Gets a number that changes whenever the language changes, for bindings that re-read text.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Gets whether the current language is written right to left.</summary>
    public bool IsRightToLeft => Language == InstallerLanguage.Persian;

    /// <summary>Gets the culture used for number and date formatting in the current language.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Catalog[(int)Language].Culture);

    /// <summary>Translates one source text into the current language.</summary>
    /// <param name="source">The Simplified Chinese source text.</param>
    public string this[string source] => Translate(source, Language);

    /// <summary>Translates a composite format and fills in its arguments.</summary>
    /// <param name="source">The Simplified Chinese source format.</param>
    /// <param name="arguments">Format arguments.</param>
    public string Format(string source, params object?[] arguments) =>
        string.Format(Culture, Translate(source, Language), arguments);

    /// <summary>
    /// Joins complete sentences the way the current language writes them: Chinese runs sentences together, every
    /// other supported language separates them with a space. Empty parts are skipped.
    /// </summary>
    /// <param name="sentences">Already translated sentences.</param>
    public string JoinSentences(params string?[] sentences)
    {
        ArgumentNullException.ThrowIfNull(sentences);
        string separator = Language is InstallerLanguage.SimplifiedChinese or InstallerLanguage.TraditionalChinese ? string.Empty : " ";
        return string.Join(separator, sentences.Where(static sentence => !string.IsNullOrEmpty(sentence)));
    }

    /// <summary>Translates one source text into the supplied language.</summary>
    /// <param name="source">The Simplified Chinese source text.</param>
    /// <param name="language">Target language.</param>
    public static string Translate(string source, InstallerLanguage language)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (language == InstallerLanguage.SimplifiedChinese) { return source; }
        return InstallerTranslations.Table.TryGetValue(source, out string[]? row) ? row[(int)language - 1] : source;
    }

    /// <summary>
    /// Chooses the installer language for a Windows display language: Chinese by script or region, the other
    /// supported languages by their neutral culture, and English for everything else.
    /// </summary>
    /// <param name="uiCulture">The Windows display language, usually <see cref="CultureInfo.CurrentUICulture"/>.</param>
    public static InstallerLanguage Detect(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        for (CultureInfo culture = uiCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            switch (culture.Name.ToUpperInvariant())
            {
                case "ZH-HANS": case "ZH-CN": case "ZH-SG": return InstallerLanguage.SimplifiedChinese;
                case "ZH-HANT": case "ZH-TW": case "ZH-HK": case "ZH-MO": return InstallerLanguage.TraditionalChinese;
                case "ZH": return InstallerLanguage.SimplifiedChinese;
                case "RU": return InstallerLanguage.Russian;
                case "FR": return InstallerLanguage.French;
                case "DE": return InstallerLanguage.German;
                case "FA": return InstallerLanguage.Persian;
                case "KO": return InstallerLanguage.Korean;
                case "EN": return InstallerLanguage.English;
            }
        }

        return InstallerLanguage.English;
    }

    private static void ValidateLanguage(InstallerLanguage language)
    {
        if (!Enum.IsDefined(language)) { throw new ArgumentOutOfRangeException(nameof(language)); }
    }
}
