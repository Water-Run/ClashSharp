using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for the rules page.</summary>
/// <remarks>
/// Invariants: Rule rows are never null and remain empty until explicit loading succeeds.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Reads the injected rule catalog only during explicit loading.
/// </remarks>
internal sealed class RulesViewModel : ObservableObject
{
    /// <summary>Localization provider used by this view model.</summary>
    private readonly IDisplayPageLocalization _localization;

    /// <summary>Rule catalog read during explicit page loading.</summary>
    private readonly IRuleCatalog _rules;

    /// <summary>Reports unexpected page-load failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Maps rule models to UI-only display rows.</summary>
    private readonly IModelDisplayMapper _displayMapper;

    /// <summary>Backing field for <see cref="Rules"/>.</summary>
    private IReadOnlyList<RulePreviewDisplay> _ruleRows = [];

    /// <summary>Initializes a rules view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="rules">Rule catalog. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> or <paramref name="rules"/> is null.</exception>
    public RulesViewModel(
        IDisplayPageLocalization localization,
        IRuleCatalog rules,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.Rules");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.Rules.Description");

    /// <summary>Gets rule preview rows.</summary>
    /// <value>Rule preview rows; never null.</value>
    public IReadOnlyList<RulePreviewDisplay> Rules
    {
        get => _ruleRows;
        private set => SetProperty(ref _ruleRows, value);
    }

    /// <summary>Loads visible rules without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return ViewModelLoadExecutor.ExecuteAsync(
            _rules.GetRules,
            ApplyRules,
            _errorSink,
            "rules-load",
            cancellationToken);
    }

    private void ApplyRules(IReadOnlyList<RulePreview> rules)
    {
        List<RulePreviewDisplay> rows = new(rules.Count);
        foreach (RulePreview rule in rules)
        {
            rows.Add(_displayMapper.Map(rule));
        }

        Rules = rows;
    }
}
