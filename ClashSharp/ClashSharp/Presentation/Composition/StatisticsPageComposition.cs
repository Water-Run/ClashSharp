using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the statistics page.</summary>
internal static class StatisticsPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context, Action openLogs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(openLogs);
        DisplayPageLocalizationAdapter localization =
            new(context.Localization);
        StatisticsStoreAdapter statistics =
            new(context.LogStorage);
        StatisticsProfilesAdapter profiles =
            new(context.Profiles);

        return new Dependencies(new StatisticsViewModel(
            localization,
            statistics,
            profiles,
            openLogs,
            context.ErrorSink,
            new ModelDisplayMapper(context.MainlandChinaTextDisplay.Apply)));
    }

    /// <summary>Injected dependencies used by the statistics view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(StatisticsViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public StatisticsViewModel ViewModel { get; }
    }
}
