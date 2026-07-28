using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>Composition boundary for startup-guide system probes and presentation.</summary>
internal static class StartupGuideComposition
{
    /// <summary>Creates a presenter backed by the current application-owned services.</summary>
    public static IStartupGuidePresenter Create(IApplicationErrorSink errorSink)
    {
        ArgumentNullException.ThrowIfNull(errorSink);

        AppSettingsService settings = AppSettingsService.Instance;
        LocalizationService localization = LocalizationService.Instance;
        IStartupCheckProbe probe = new LegacyStartupCheckProbe(
            settings,
            ProfileCatalogService.Instance,
            MihomoServiceManager.Instance,
            StartupRestoreFallbackService.Instance,
            WindowsProxyService.Instance,
            ProxyRecoveryService.Instance);
        StartupCheckService checks = new(
            probe,
            localization.GetString,
            errorSink);
        return new StartupGuidePresenter(
            checks,
            localization.GetString,
            errorSink);
    }

    /// <summary>Adapts legacy process-wide services to background-safe startup probes.</summary>
    private sealed class LegacyStartupCheckProbe(
        AppSettingsService settings,
        ProfileCatalogService profileCatalog,
        MihomoServiceManager mihomoServiceManager,
        StartupRestoreFallbackService startupRestoreFallback,
        WindowsProxyService windowsProxy,
        ProxyRecoveryService proxyRecovery) : IStartupCheckProbe
    {
        public bool HasSubscription(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return profileCatalog.GetSubscriptionLinks().Count > 0;
        }

        public bool IsTransparentProxyEnabled(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return settings.TransparentProxyEnabled;
        }

        public MihomoServiceStatus GetMihomoStatus(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return mihomoServiceManager.GetLatestStatus();
        }

        public bool IsFallbackRegistered(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return startupRestoreFallback.IsRegistered();
        }

        public WindowsProxyState GetWindowsProxyState(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return windowsProxy.GetCurrentState();
        }

        public int GetMixedPort(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return settings.MixedPort;
        }

        public bool IsStaleProxy(
            WindowsProxyState state,
            int mixedPort,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return proxyRecovery.IsStaleClashProxy(state, mixedPort);
        }
    }
}
