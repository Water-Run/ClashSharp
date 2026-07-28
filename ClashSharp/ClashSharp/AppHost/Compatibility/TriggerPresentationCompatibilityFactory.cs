using System;
using System.Linq;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>
/// Host-owned transition boundary for pages that WinUI still creates through parameterless constructors.
/// </summary>
/// <remarks>
/// This is the only permitted trigger presentation lookup. Durable state and CRUD remain owned by the
/// injected application facade; the boundary can be removed when page activation is DI-owned.
/// </remarks>
internal sealed class TriggerPresentationCompatibilityFactory : IDisposable
{
    private static TriggerPresentationCompatibilityFactory? _active;
    private readonly ITriggerDefinitionStore _store;
    private readonly AppSettingsService _settings;
    private int _isActive;

    public TriggerPresentationCompatibilityFactory(
        ITriggerDefinitionStore store,
        AppSettingsService settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Activates this host-scoped factory before any WinUI page can be constructed.</summary>
    public void Activate()
    {
        if (Interlocked.CompareExchange(ref _isActive, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _active, this, null) is not null)
        {
            Interlocked.Exchange(ref _isActive, 0);
            throw new InvalidOperationException("A trigger presentation host is already active.");
        }
    }

    /// <summary>Gets the active host boundary used by WinUI-created presentation objects.</summary>
    public static TriggerPresentationCompatibilityFactory RequireActive()
    {
        return Volatile.Read(ref _active)
            ?? throw new InvalidOperationException("The trigger presentation host is not active.");
    }

    /// <summary>Creates a page-owned list ViewModel over the shared asynchronous facade.</summary>
    public TriggersViewModel CreateViewModel(
        Func<string, string> getString,
        IApplicationErrorSink errorSink)
    {
        EnsureActive();
        return new TriggersViewModel(
            getString,
            _store,
            new PresentationSettings(_settings),
            errorSink);
    }

    /// <summary>Returns trigger counts from the latest successfully initialized facade cache.</summary>
    public TriggerPresentationSummary GetSummary()
    {
        EnsureActive();
        TriggerDefinitionCatalog catalog = _store.Current;
        return new TriggerPresentationSummary(
            catalog.Tasks.Count,
            catalog.Tasks.Count(static task => task.Definition.IsEnabled));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isActive, 0) == 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref _active, null, this);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _isActive) == 0
            || !ReferenceEquals(Volatile.Read(ref _active), this))
        {
            throw new InvalidOperationException("The trigger presentation host is not active.");
        }
    }

    private sealed class PresentationSettings(AppSettingsService settings)
        : ITriggerPresentationSettings
    {
        private readonly AppSettingsService _settings = settings
            ?? throw new ArgumentNullException(nameof(settings));

        public bool IsEnabled
        {
            get => _settings.TriggersEnabled;
            set => _settings.TriggersEnabled = value;
        }
    }
}
