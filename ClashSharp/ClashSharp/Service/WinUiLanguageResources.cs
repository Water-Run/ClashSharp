using ClashSharp.Model;
using Microsoft.Windows.Globalization;

namespace ClashSharp.Service;

/// <summary>Connects the catalog selection to the Windows App SDK resource language on the owning UI thread.</summary>
internal static class WinUiLanguageResources
{
    /// <summary>Applies the configured language to native controls and then application text bindings.</summary>
    /// <param name="localization">Application-owned catalog resolver.</param>
    /// <param name="language">Configured language, including automatic detection.</param>
    public static void Apply(LocalizationService localization, AppLanguage language) =>
        CreateCoordinator(localization).Apply(language);

    /// <summary>Observes a language selection whose native resource override has been verified.</summary>
    /// <param name="localization">Application-owned catalog resolver.</param>
    public static AppLanguage CaptureLanguage(LocalizationService localization) =>
        CreateCoordinator(localization).CaptureLanguage();

    // Packaged applications persist this native override. It remains a projection of the
    // authoritative preference and is re-derived at startup, including AutoDetect, instead
    // of using the previous override to infer the user's configured language.
    private static LanguageResourceCoordinator CreateCoordinator(LocalizationService localization) =>
        new(localization, () => ApplicationLanguages.PrimaryLanguageOverride,
            value => ApplicationLanguages.PrimaryLanguageOverride = value);
}
