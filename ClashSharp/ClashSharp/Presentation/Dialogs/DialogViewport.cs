using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Supplies monitor work-area limits for modal content independently of the owner's content size.</summary>
internal static class DialogViewport
{
    internal static Size GetAvailableSize(XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var work = DisplayArea.GetFromWindowId(root.ContentIslandEnvironment.AppWindowId, DisplayAreaFallback.Nearest).WorkArea;
        double scale = root.RasterizationScale;
        return new Size(Math.Max(1, work.Width / scale - 32), Math.Max(1, work.Height / scale - 32));
    }
}
