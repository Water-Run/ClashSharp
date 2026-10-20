using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>Refreshes only the currently displayed page and releases the language subscription with its window.</summary>
internal sealed class CurrentPageLocalizationBinding : IDisposable
{
    private LocalizationService? _localization;
    private readonly Func<ILocalizedPresentation?> _current;

    public CurrentPageLocalizationBinding(LocalizationService localization, Func<ILocalizedPresentation?> current)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        localization.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _localization) is not null) { _current()?.RefreshLocalization(); }
    }

    public void Dispose()
    {
        LocalizationService? localization = Interlocked.Exchange(ref _localization, null);
        if (localization is not null) { localization.LanguageChanged -= OnLanguageChanged; }
    }
}
