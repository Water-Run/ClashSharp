using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the profiles page.</summary>
internal static class ProfilesPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProfilesViewModel viewModel = new(
            context.Localization.GetString,
            new ProfileManagementCatalogAdapter(context.Profiles),
            new PageLogAdapter(context.LogStorage),
            () => context.Settings.ActiveProfileId,
            context.ErrorSink,
            new ModelDisplayMapper(context.MainlandChinaTextDisplay.Apply));

        return new Dependencies(
            viewModel,
            context.Localization.GetString,
            () => context.LogStorage.AppendLog(
                "Warning",
                "Profiles",
                context.Localization.GetString("Profiles.Log.FilePickerNoMainWindow"),
                null),
            context.ErrorSink,
            changed => new DataGenerationSubscription(context.Settings, changed));
    }

    /// <summary>Injected dependencies used by the profiles view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            ProfilesViewModel viewModel,
            Func<string, string> getString,
            Action reportFilePickerUnavailable,
            IApplicationErrorSink errorSink,
            Func<Action, IDisposable> subscribeToDataChanges)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ReportFilePickerUnavailable = reportFilePickerUnavailable
                ?? throw new ArgumentNullException(nameof(reportFilePickerUnavailable));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            SubscribeToDataChanges = subscribeToDataChanges ?? throw new ArgumentNullException(nameof(subscribeToDataChanges));
        }

        public ProfilesViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public Action ReportFilePickerUnavailable { get; }

        public IApplicationErrorSink ErrorSink { get; }
        public Func<Action, IDisposable> SubscribeToDataChanges { get; }
    }
}
