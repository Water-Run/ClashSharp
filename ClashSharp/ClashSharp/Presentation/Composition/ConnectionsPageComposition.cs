using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the connections page.</summary>
internal static class ConnectionsPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ConnectionsViewModel viewModel = new(
            new ConnectionsLocalizationAdapter(context.Localization),
            new ActiveConnectionClientAdapter(context.MihomoConnections),
            new ConnectionLogAdapter(context.LogStorage),
            context.ErrorSink,
            context.MainlandChinaTextDisplay.Apply);

        return new Dependencies(viewModel, context.ErrorSink, changed => new DataGenerationSubscription(context.Settings, changed));
    }

    /// <summary>Injected dependencies used by the connections view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(ConnectionsViewModel viewModel, IApplicationErrorSink errorSink, Func<Action, IDisposable> subscribeToDataChanges)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            ErrorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
            SubscribeToDataChanges = subscribeToDataChanges ?? throw new ArgumentNullException(nameof(subscribeToDataChanges));
        }

        public ConnectionsViewModel ViewModel { get; }

        public IApplicationErrorSink ErrorSink { get; }
        public Func<Action, IDisposable> SubscribeToDataChanges { get; }
    }
}
