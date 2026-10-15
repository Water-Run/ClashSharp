using System;
using System.Collections.Generic;
using System.Globalization;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Publishes an application language only after its native resource override is independently observed.</summary>
/// <remarks>Callers own the UI thread. A failed publication restores both native resources and the catalog selection.</remarks>
internal sealed class LanguageResourceCoordinator(
    LocalizationService localization,
    Func<string> readOverride,
    Action<string> writeOverride)
{
    private readonly LocalizationService _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    private readonly Func<string> _readOverride = readOverride ?? throw new ArgumentNullException(nameof(readOverride));
    private readonly Action<string> _writeOverride = writeOverride ?? throw new ArgumentNullException(nameof(writeOverride));

    /// <summary>Reads the catalog selection only when the native resource language agrees.</summary>
    public AppLanguage CaptureLanguage()
    {
        AppLanguage language = _localization.CurrentLanguage;
        RequireOverride(LocalizationService.ResolveResourceLanguageTag(language, CultureInfo.CurrentUICulture));
        return language;
    }

    /// <summary>Installs and verifies framework resources before notifying application text bindings.</summary>
    /// <param name="language">Canonical selection; automatic detection uses the same UI culture as the catalogs.</param>
    public void Apply(AppLanguage language)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string target = LocalizationService.ResolveResourceLanguageTag(language, culture);
        AppLanguage before = _localization.CurrentLanguage;
        string previousOverride = _readOverride();
        // The pinned MRT Core API rejects an empty override. On first use, the prior catalog's
        // effective language restores equivalent resource behavior without submitting an invalid tag.
        string previous = string.IsNullOrEmpty(previousOverride)
            ? LocalizationService.ResolveResourceLanguageTag(before, culture)
            : previousOverride;
        try
        {
            InstallOverride(target);
            _localization.CurrentLanguage = language;
            RequireOverride(target);
            if (_localization.CurrentLanguage != language) { throw new InvalidOperationException("The selected language changed during publication."); }
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            List<Exception> failures = [failure];
            Restore(() => InstallOverride(previous), failures);
            Restore(() => _localization.CurrentLanguage = before, failures);
            Restore(() =>
            {
                RequireOverride(previous);
                if (_localization.CurrentLanguage != before) { throw new InvalidOperationException("The previous language could not be restored."); }
            }, failures);
            if (failures.Count > 1) { throw new AggregateException("Language publication and its restoration failed.", failures); }
            throw;
        }
    }

    private void InstallOverride(string languageTag)
    {
        if (!MatchesOverride(languageTag)) { _writeOverride(languageTag); }
        RequireOverride(languageTag);
    }

    private bool MatchesOverride(string languageTag) =>
        StringComparer.OrdinalIgnoreCase.Equals(_readOverride(), languageTag);

    private void RequireOverride(string languageTag)
    {
        if (!MatchesOverride(languageTag)) { throw new InvalidOperationException("The native resource language could not be verified."); }
    }

    private static void Restore(Action restore, ICollection<Exception> failures)
    {
        try { restore(); }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { failures.Add(failure); }
    }
}
