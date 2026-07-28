using System;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Builds the explicit dependency graph for the about page.</summary>
internal static class AboutPageComposition
{
    /// <summary>Creates dependencies from the current application-owned services.</summary>
    public static Dependencies Create()
    {
        LocalizationService localization = LegacyPageServiceBridge.Localization;
        AppSettingsService settings = LegacyPageServiceBridge.Settings;
        CoreConfigurationService coreConfiguration = LegacyPageServiceBridge.CoreConfiguration;
        MihomoCoreService core = LegacyPageServiceBridge.MihomoCore;
        AboutViewModel viewModel = new(
            new DisplayPageLocalizationAdapter(localization),
            new AboutCoreAdapter(core),
            new WindowsUriLauncher(),
            LegacyPageServiceBridge.CreateErrorSink());

        return new Dependencies(
            viewModel,
            localization.GetString,
            () =>
            {
                CoreConfigurationState configuration = coreConfiguration.GetState();
                return new ProxyInformation(
                    settings.MixedPort,
                    configuration.ConfigPath,
                    core.IsBinaryAvailable,
                    core.BinaryPath);
            });
    }

    /// <summary>Injected dependencies used by the about view.</summary>
    internal sealed class Dependencies
    {
        public Dependencies(
            AboutViewModel viewModel,
            Func<string, string> getString,
            Func<ProxyInformation> readProxyInformation)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            GetString = getString ?? throw new ArgumentNullException(nameof(getString));
            ReadProxyInformation = readProxyInformation
                ?? throw new ArgumentNullException(nameof(readProxyInformation));
        }

        public AboutViewModel ViewModel { get; }

        public Func<string, string> GetString { get; }

        public Func<ProxyInformation> ReadProxyInformation { get; }
    }

    /// <summary>Immutable proxy and core information displayed by the about view.</summary>
    internal sealed record ProxyInformation(
        int MixedPort,
        string ConfigPath,
        bool IsCoreBinaryAvailable,
        string CoreBinaryPath);
}
