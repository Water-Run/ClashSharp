using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the subscription-links page.</summary>
internal static class LinksPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        LocalizationService localization = LegacyPageServiceBridge.Localization;
        LinksViewModel viewModel = new(
            localization.GetString,
            new SubscriptionLinkCatalogAdapter(LegacyPageServiceBridge.Profiles),
            new PageLogAdapter(LegacyPageServiceBridge.LogStorage),
            LegacyPageServiceBridge.CreateErrorSink(),
            new ModelDisplayMapper(LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply));

        return new Dependencies(viewModel, localization.GetString);
    }

    /// <summary>Injected dependencies used by the subscription-links view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(LinksViewModel viewModel, Func<string, string> getString)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
        }

        public LinksViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }
    }
}
