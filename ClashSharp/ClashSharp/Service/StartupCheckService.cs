/*
 * Startup Check Service
 * Builds user-facing startup health checks for the startup prompt
 *
 * @author: WaterRun
 * @file: Service/StartupCheckService.cs
 * @date: 2026-06-25
 */

using System;
using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>One user-facing startup check result.</summary>
/// <param name="IsHealthy">True when the check passes.</param>
/// <param name="Title">Localized check title.</param>
/// <param name="Description">Localized check detail.</param>
public readonly record struct StartupCheckItem(bool IsHealthy, string Title, string Description);

/// <summary>Builds startup health checks from current application state.</summary>
public sealed class StartupCheckService
{
    public static StartupCheckService Instance { get; } = new();

    private StartupCheckService()
    {
    }

    /// <summary>Gets startup checks for subscription, transparent proxy, fallback restore, and stale proxy state.</summary>
    public IReadOnlyList<StartupCheckItem> GetChecks()
    {
        List<StartupCheckItem> checks = [];
        Func<string, string> getString = LocalizationService.Instance.GetString;
        AppSettingsService settings = AppSettingsService.Instance;

        checks.Add(BuildSubscriptionCheck(getString));
        checks.Add(BuildTransparentProxyCheck(settings, getString));
        checks.Add(BuildFallbackCheck(getString));
        checks.Add(BuildStaleProxyCheck(settings, getString));
        return checks;
    }

    private static StartupCheckItem BuildSubscriptionCheck(Func<string, string> getString)
    {
        try
        {
            bool hasSubscription = ProfileCatalogService.Instance.GetSubscriptionLinks().Count > 0;
            return new StartupCheckItem(
                hasSubscription,
                getString("StartupPrompt.Check.Subscription.Title"),
                getString(hasSubscription
                    ? "StartupPrompt.Check.Subscription.Ready"
                    : "StartupPrompt.Check.Subscription.Missing"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupCheckItem(false, getString("StartupPrompt.Check.Subscription.Title"), exception.Message);
        }
    }

    private static StartupCheckItem BuildTransparentProxyCheck(AppSettingsService settings, Func<string, string> getString)
    {
        if (!settings.TransparentProxyEnabled)
        {
            return new StartupCheckItem(
                true,
                getString("StartupPrompt.Check.TransparentProxy.Title"),
                getString("StartupPrompt.Check.TransparentProxy.Disabled"));
        }

        try
        {
            MihomoServiceStatus status = MihomoServiceManager.Instance.GetStatus();
            return new StartupCheckItem(
                status.IsInstalled,
                getString("StartupPrompt.Check.TransparentProxy.Title"),
                status.IsInstalled
                    ? status.Message
                    : getString("StartupPrompt.Check.TransparentProxy.Missing"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupCheckItem(false, getString("StartupPrompt.Check.TransparentProxy.Title"), exception.Message);
        }
    }

    private static StartupCheckItem BuildFallbackCheck(Func<string, string> getString)
    {
        try
        {
            bool isRegistered = StartupRestoreFallbackService.Instance.IsRegistered();
            return new StartupCheckItem(
                isRegistered,
                getString("StartupPrompt.Check.Fallback.Title"),
                getString(isRegistered
                    ? "StartupPrompt.Check.Fallback.Registered"
                    : "StartupPrompt.Check.Fallback.NotRegistered"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupCheckItem(false, getString("StartupPrompt.Check.Fallback.Title"), exception.Message);
        }
    }

    private static StartupCheckItem BuildStaleProxyCheck(AppSettingsService settings, Func<string, string> getString)
    {
        try
        {
            WindowsProxyState state = WindowsProxyService.Instance.GetCurrentState();
            bool hasStaleProxy = ProxyRecoveryService.Instance.IsStaleClashProxy(state, settings.MixedPort);
            return new StartupCheckItem(
                !hasStaleProxy,
                getString("StartupPrompt.Check.StaleProxy.Title"),
                getString(hasStaleProxy
                    ? "StartupPrompt.Check.StaleProxy.Detected"
                    : "StartupPrompt.Check.StaleProxy.Clean"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return new StartupCheckItem(false, getString("StartupPrompt.Check.StaleProxy.Title"), exception.Message);
        }
    }
}
