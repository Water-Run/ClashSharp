using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Hosting.Settings;
using ClashSharp.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashSharp.Hosting.Data;

/// <summary>Supplies the live startup window without allowing generation services to own or recreate it.</summary>
internal sealed record AppGenerationUi(
    Func<FrameworkElement?> GetRoot, DispatcherQueue Queue, Func<bool> ExitRequested, CancellationToken WindowLifetime)
{
    public OwnedUiDispatcher CreateDispatcher() => WinUiAppearanceSettings.CreateDispatcher(Queue, WindowLifetime);
    public WinUiAppearanceSettings CreateAppearance(LocalizationService localization) => new(GetRoot, localization);
}
