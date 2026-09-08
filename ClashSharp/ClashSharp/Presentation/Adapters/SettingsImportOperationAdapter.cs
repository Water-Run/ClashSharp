using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Connects an admitted import to existing settings, profile, and runtime adapters.</summary>
internal sealed class SettingsImportOperationAdapter(
    AppSettingsService settings,
    LocalizationService localization,
    ProfileCatalogService profiles,
    ISettingsDestructiveRuntimeScope runtimeMutation) : ISettingsImportOperation
{
    public SettingsRuntimeSnapshot CaptureSnapshot() => new(
        settings.DisplayLanguage,
        settings.AppThemeMode,
        settings.AppAccentColorMode,
        settings.AppAccentColorValue,
        settings.LaunchAtStartupEnabled,
        settings.ConnectionSamplingEnabled,
        settings.ConnectionSamplingIntervalSeconds,
        settings.CurrentMode,
        settings.ActiveProfileId,
        settings.TransparentProxyEnabled,
        settings.MixedPort);

    public async Task<IRetainedSettingsTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken) =>
        await runtimeMutation.BeginImportAsync(packagePath, cancellationToken);

    public void InvalidateProfiles() => profiles.ResetAfterDataDeletion();

    public void ApplyLanguage(AppLanguage language) => localization.CurrentLanguage = language;

    public void ApplyTheme(AppThemeMode theme) => AppThemeService.Apply(theme);

    public void ApplyAccentColor(AppAccentColorMode mode, string value) => AppThemeService.ApplyAccentColor(mode, value);

    public Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken) =>
        runtimeMutation.ApplyLaunchAtStartupAsync(enabled, cancellationToken);

    public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken) =>
        runtimeMutation.RestartConnectionSamplingAsync(cancellationToken);

    public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken) =>
        runtimeMutation.ApplyNetworkSettingsAsync(transparentProxyEnabled, mixedPort, cancellationToken);
}
