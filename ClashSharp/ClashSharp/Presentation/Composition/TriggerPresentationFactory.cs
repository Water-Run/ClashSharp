using System;
using System.Linq;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Creates trigger presentation state from AppHost-owned application services.</summary>
internal sealed class TriggerPresentationFactory
{
    private readonly ITriggerDefinitionStore _store;
    private readonly AppSettingsService _settings;
    private TriggersViewModel? _viewModel;

    public TriggerPresentationFactory(
        ITriggerDefinitionStore store,
        AppSettingsService settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public TriggersViewModel CreateViewModel(
        Func<string, string> getString,
        IApplicationErrorSink errorSink)
    {
        // Navigation recreates the page, but its unsaved draft belongs to the application session.
        // Loading a new page refreshes the catalog while preserving this editor and its conflict token.
        return _viewModel ??= new TriggersViewModel(
            getString,
            _store,
            new PresentationSettings(_settings),
            errorSink);
    }

    public TriggerPresentationSummary GetSummary()
    {
        TriggerDefinitionCatalog catalog = _store.Current;
        return new TriggerPresentationSummary(
            catalog.Tasks.Count,
            catalog.Tasks.Count(static task => task.Definition.IsEnabled));
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
