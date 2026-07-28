using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="LocalizationService"/> to display page localization.</summary>
/// <remarks>
/// Invariants: Wraps a non-null localization service.
/// Thread safety: Matches the wrapped service and is intended for UI-thread use.
/// Side effects: Reads localized resource strings.
/// </remarks>
internal sealed class DisplayPageLocalizationAdapter : IDisplayPageLocalization
{
    /// <summary>Wrapped localization service.</summary>
    private readonly LocalizationService _localization;

    /// <summary>Initializes a display page localization adapter.</summary>
    /// <param name="localization">Localization service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> is null.</exception>
    public DisplayPageLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}
