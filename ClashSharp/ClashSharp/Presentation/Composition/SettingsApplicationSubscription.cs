using System;
using System.Threading;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

internal sealed class SettingsApplicationSubscription : IDisposable
{
    private AppSettingsService? _settings;
    private readonly Action _changed;
    public SettingsApplicationSubscription(AppSettingsService settings, Action changed)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        settings.SettingsApplicationStateChanged += OnChanged;
    }
    private void OnChanged(object? sender, EventArgs args) => _changed();
    public void Dispose()
    {
        AppSettingsService? settings = Interlocked.Exchange(ref _settings, null);
        if (settings is not null) { settings.SettingsApplicationStateChanged -= OnChanged; }
    }
}
