using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the rules page.</summary>
internal static class RulesPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        RulesViewModel viewModel = new(
            new DisplayPageLocalizationAdapter(LegacyPageServiceBridge.Localization),
            new RuleCatalogAdapter(LegacyPageServiceBridge.Rules),
            LegacyPageServiceBridge.CreateErrorSink(),
            new ModelDisplayMapper(LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply));
        return new Dependencies(viewModel);
    }

    /// <summary>Injected dependencies used by the rules view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(RulesViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public RulesViewModel ViewModel { get; }
    }
}
