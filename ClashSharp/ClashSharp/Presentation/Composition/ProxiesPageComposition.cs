using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the proxies page.</summary>
internal static class ProxiesPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        ProxiesViewModel viewModel = new(
            new ProxiesLocalizationAdapter(LegacyPageServiceBridge.Localization),
            new ProxyNodeCatalogAdapter(LegacyPageServiceBridge.ProxyNodes),
            new ProxyLatencyTesterAdapter(LegacyPageServiceBridge.ProxyLatency),
            new ProxyRuntimeControllerAdapter(LegacyPageServiceBridge.MihomoController),
            new ProxiesLogAdapter(LegacyPageServiceBridge.LogStorage),
            LegacyPageServiceBridge.CreateErrorSink(),
            new ModelDisplayMapper(LegacyPageServiceBridge.MainlandChinaTextDisplay.Apply));

        return new Dependencies(viewModel);
    }

    /// <summary>Injected dependencies used by the proxies view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(ProxiesViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public ProxiesViewModel ViewModel { get; }
    }
}
