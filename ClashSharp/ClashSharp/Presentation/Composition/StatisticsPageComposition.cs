using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the statistics page.</summary>
internal static class StatisticsPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        DisplayPageLocalizationAdapter localization =
            new(LegacyPageServiceBridge.Localization);
        StatisticsStoreAdapter statistics =
            new(LegacyPageServiceBridge.LogStorage);
        StatisticsProfilesAdapter profiles =
            new(LegacyPageServiceBridge.Profiles);
        IApplicationErrorSink errorSink = LegacyPageServiceBridge.CreateErrorSink();

        return new Dependencies(openLogs =>
            new StatisticsViewModel(
                localization,
                statistics,
                profiles,
                openLogs,
                errorSink,
                new ModelDisplayMapper(LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply)));
    }

    /// <summary>Injected dependencies used by the statistics view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(Func<Action, StatisticsViewModel> createViewModel)
        {
            CreateViewModel = createViewModel
                ?? throw new ArgumentNullException(nameof(createViewModel));
        }

        public Func<Action, StatisticsViewModel> CreateViewModel { get; }
    }
}
