using System;
using System.Threading;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>AppHost-owned factory for startup-guide system probes and presentation.</summary>
internal sealed class StartupGuideComposition(
    AppSettingsService settings,
    LocalizationService localization,
    ProfileCatalogService profileCatalog,
    MihomoServiceManager mihomoServiceManager,
    StartupRestoreFallbackService startupRestoreFallback,
    WindowsProxyService windowsProxy,
    ProxyRecoveryService proxyRecovery)
{
    /// <summary>Creates a presenter backed by explicitly composed application services.</summary>
    public IStartupGuidePresenter Create(IApplicationErrorSink errorSink)
    {
        ArgumentNullException.ThrowIfNull(errorSink);

        IStartupCheckProbe probe = new StartupCheckProbe(
            settings,
            profileCatalog,
            mihomoServiceManager,
            startupRestoreFallback,
            windowsProxy,
            proxyRecovery);
        StartupCheckService checks = new(
            probe,
            localization.GetString,
            errorSink);
        return new StartupGuidePresenter(
            checks,
            localization.GetString,
            errorSink);
    }

    /// <summary>Adapts application services to background-safe startup probes.</summary>
    private sealed class StartupCheckProbe(
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
