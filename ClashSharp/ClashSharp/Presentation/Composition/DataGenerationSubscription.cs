using System;
using System.Threading;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>Keeps a page's data-directory refresh subscription within its visible lifetime.</summary>
internal sealed class DataGenerationSubscription : IDisposable
{
    private AppSettingsService? _settings;
    private readonly Action _changed;

    public DataGenerationSubscription(AppSettingsService settings, Action changed)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        settings.DataGenerationChanged += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs args) => _changed();

    public void Dispose()
    {
        AppSettingsService? settings = Interlocked.Exchange(ref _settings, null);
        if (settings is not null) { settings.DataGenerationChanged -= OnChanged; }
    }
}
