using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the connections page.</summary>
internal static class ConnectionsPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        ConnectionsViewModel viewModel = new(
            new ConnectionsLocalizationAdapter(LegacyPageServiceBridge.Localization),
            new ActiveConnectionClientAdapter(LegacyPageServiceBridge.MihomoConnections),
            new ConnectionLogAdapter(LegacyPageServiceBridge.LogStorage),
            LegacyPageServiceBridge.CreateErrorSink(),
            LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply);

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
