using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the profiles page.</summary>
internal static class ProfilesPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        LocalizationService localization = LegacyPageServiceBridge.Localization;
        LogStorageService logStorage = LegacyPageServiceBridge.LogStorage;
        ProfilesViewModel viewModel = new(
            localization.GetString,
            new ProfileManagementCatalogAdapter(LegacyPageServiceBridge.Profiles),
            new PageLogAdapter(logStorage),
            () => LegacyPageServiceBridge.Settings.ActiveProfileId,
            LegacyPageServiceBridge.CreateErrorSink(),
            new ModelDisplayMapper(LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply));

        return new Dependencies(
            viewModel,
            localization.GetString,
            () => logStorage.AppendLog(
                "Warning",
                "Profiles",
                localization.GetString("Profiles.Log.FilePickerNoMainWindow"),
                null));
    }

    /// <summary>Injected dependencies used by the profiles view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            ProfilesViewModel viewModel,
            Func<string, string> getString,
            Action reportFilePickerUnavailable)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ReportFilePickerUnavailable = reportFilePickerUnavailable
                ?? throw new ArgumentNullException(nameof(reportFilePickerUnavailable));
        }

        public ProfilesViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public Action ReportFilePickerUnavailable { get; }
    }
}
