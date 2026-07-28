using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the logs page.</summary>
internal static class LogsPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        LocalizationService localization = LegacyPageServiceBridge.Localization;
        IApplicationErrorSink errorSink = LegacyPageServiceBridge.CreateErrorSink();
        LogsViewModel viewModel = new(
            localization.GetString,
            new LogManagementStoreAdapter(LegacyPageServiceBridge.LogStorage),
            errorSink);
        return new Dependencies(viewModel, localization.GetString, errorSink);
    }

    /// <summary>Injected dependencies used by the logs view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            LogsViewModel viewModel,
            Func<string, string> getString,
            IApplicationErrorSink errorSink)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        }

        public LogsViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public IApplicationErrorSink ErrorSink { get; }
    }
}
