using System;
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

        return new Dependencies(viewModel);
    }

    /// <summary>Injected dependencies used by the connections view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(ConnectionsViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public ConnectionsViewModel ViewModel { get; }
    }
}
