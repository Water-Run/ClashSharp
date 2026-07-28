using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="LocalizationService"/> to shell-localization needs.</summary>
internal sealed class ShellLocalizationAdapter : IShellLocalization
{
    private readonly LocalizationService _localization;

    public ShellLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    public event EventHandler? LanguageChanged
    {
        add => _localization.LanguageChanged += value;
        remove => _localization.LanguageChanged -= value;
    }

    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}
