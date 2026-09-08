using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashSharp.Hosting.Settings;

/// <summary>Adapts the actual window, localization resolver and accent resources without preference access.</summary>
internal sealed class WinUiAppearanceSettings(Func<FrameworkElement?> getRoot, LocalizationService localization) : IAppearanceNativeSettings
{
    private readonly Func<FrameworkElement?> _getRoot = getRoot ?? throw new ArgumentNullException(nameof(getRoot));
    private readonly LocalizationService _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    /// <summary>Creates a generation-owned operation boundary whose lifetime ends before the window's queue closes.</summary>
    internal static OwnedUiDispatcher CreateDispatcher(DispatcherQueue queue, CancellationToken windowLifetime)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return new(() => queue.HasThreadAccess, operation => queue.TryEnqueue(() => operation()), windowLifetime);
    }

    public AppearanceNativeConfiguration CaptureConfiguration()
    {
        FrameworkElement root = RequireRoot();
        AppThemeMode theme = root.RequestedTheme switch
        {
            ElementTheme.Default => AppThemeMode.FollowSystem,
            ElementTheme.Light => AppThemeMode.Light,
            ElementTheme.Dark => AppThemeMode.Dark,
            _ => throw new InvalidOperationException("The window requested an unsupported theme."),
        };
        return new(_localization.CurrentLanguage, theme, AppThemeService.ReadAccentConfiguration());
    }

    public void ApplyLanguage(AppLanguage language) { _ = RequireRoot(); _localization.CurrentLanguage = language; }
    public void ApplyTheme(AppThemeMode theme) => AppThemeService.Apply(RequireRoot(), theme);
    public void ApplyAccent(AccentColorConfiguration accent)
    {
        _ = RequireRoot();
        AppThemeService.ApplyAccentColor(accent.Mode, accent.ColorValue);
    }

    private FrameworkElement RequireRoot()
    {
        FrameworkElement root = _getRoot() ?? throw new InvalidOperationException("The appearance window is unavailable.");
        if (!root.DispatcherQueue.HasThreadAccess) { throw new InvalidOperationException("Appearance requires the owning window thread."); }
        return root;
    }
}
