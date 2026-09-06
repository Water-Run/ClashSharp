using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the subscription-links page.</summary>
internal static class LinksPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        LinksViewModel viewModel = new(
            context.Localization.GetString,
            new SubscriptionLinkCatalogAdapter(context.Profiles),
            new PageLogAdapter(context.LogStorage),
            context.ErrorSink,
            new ModelDisplayMapper(context.MainlandChinaTextDisplay.Apply));

        return new Dependencies(viewModel, context.Localization.GetString, context.ErrorSink);
    }

    /// <summary>Injected dependencies used by the subscription-links view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(LinksViewModel viewModel, Func<string, string> getString, IApplicationErrorSink errorSink)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        }

        public LinksViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public IApplicationErrorSink ErrorSink { get; }
    }
}
