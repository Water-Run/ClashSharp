using System;
using ClashSharp.Presentation.Adapters;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the proxies page.</summary>
internal static class ProxiesPageComposition
{
    /// <summary>Creates dependencies from the AppHost-owned page context.</summary>
    public static Dependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProxiesViewModel viewModel = new(
            new ProxiesLocalizationAdapter(context.Localization),
            new ProxyNodeCatalogAdapter(context.ProxyNodes),
            new ProxyLatencyTesterAdapter(context.ProxyLatency),
            new ProxyRuntimeControllerAdapter(context.MihomoController),
            new ProxiesLogAdapter(context.LogStorage),
            context.ErrorSink,
            new ModelDisplayMapper(context.MainlandChinaTextDisplay.Apply));

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
