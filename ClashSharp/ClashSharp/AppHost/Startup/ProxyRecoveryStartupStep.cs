using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Completes best-effort stale proxy recovery before window startup.</summary>
internal sealed class ProxyRecoveryStartupStep : IStartupStep
{
    public string Name => "proxy-recovery";

    public int Order => 300;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        return Task.Run(ApplyRecovery, cancellationToken);
    }

    private static StartupStepResult ApplyRecovery()
    {
        try
        {
            ProxyRecoveryResult result = ProxyRecoveryService.Instance.ApplyStartupRecoveryIfNeeded();
            if (result.WasApplied)
            {
                LogStorageService.Instance.AppendLog("Info", "ProxyRecovery", result.Message, null);
            }

            return StartupStepResult.Succeeded();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "ProxyRecovery",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
            return StartupStepResult.Warning("ProxyRecovery.StartupFailed");
        }
    }
}
