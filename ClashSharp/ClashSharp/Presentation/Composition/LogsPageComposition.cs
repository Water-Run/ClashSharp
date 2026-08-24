using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the logs page.</summary>
internal static class LogsPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(
        PageCompositionContext context,
        string? initialSourceFilter,
        Action navigateBack)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(navigateBack);
        LogsViewModel viewModel = new(
            context.Localization.GetString,
            new LogManagementStoreAdapter(context.LogStorage),
            context.ErrorSink,
            context.MihomoController.StreamLogsAsync,
            context.MihomoService.ReadHostLogsAsync);
        return new Dependencies(
            viewModel,
            context.Localization.GetString,
            context.ErrorSink,
            initialSourceFilter,
            navigateBack);
    }

    /// <summary>Injected dependencies used by the logs view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            LogsViewModel viewModel,
            Func<string, string> getString,
            IApplicationErrorSink errorSink,
            string? initialSourceFilter,
            Action navigateBack)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            InitialSourceFilter = initialSourceFilter;
            NavigateBack = navigateBack ?? throw new ArgumentNullException(nameof(navigateBack));
        }

        public LogsViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public IApplicationErrorSink ErrorSink { get; }

        public string? InitialSourceFilter { get; }

        public Action NavigateBack { get; }
    }
}
