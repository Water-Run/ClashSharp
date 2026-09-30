using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the rules page.</summary>
internal static class RulesPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RulesViewModel viewModel = new(
            new DisplayPageLocalizationAdapter(context.Localization),
            new RuleCatalogAdapter(context.Rules),
            context.ErrorSink,
            new ModelDisplayMapper(context.MainlandChinaTextDisplay.Apply));
        return new Dependencies(viewModel, context.ErrorSink,
            changed => new DataGenerationSubscription(context.Settings, changed));
    }

    /// <summary>Injected dependencies used by the rules view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(RulesViewModel viewModel, IApplicationErrorSink errorSink, Func<Action, IDisposable> subscribeToDataChanges)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            SubscribeToDataChanges = subscribeToDataChanges ?? throw new ArgumentNullException(nameof(subscribeToDataChanges));
        }

        public RulesViewModel ViewModel { get; }
        public IApplicationErrorSink ErrorSink { get; }
        public Func<Action, IDisposable> SubscribeToDataChanges { get; }
    }
}
