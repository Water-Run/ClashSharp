using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Runs the login restore helper without constructing the normal application shell.</summary>
internal sealed class StartupRestoreFallbackStep : IStartupStep
{
    public string Name => "startup-restore-fallback";

    public int Order => 200;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Arguments.Contains(StartupRestoreFallbackService.HelperArgument, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        try
        {
            ProxyRecoveryResult result = StartupRestoreFallbackService.Instance.RunRestoreOnce();
            if (result.WasApplied)
            {
                LogStorageService.Instance.AppendLog("Info", "StartupRestoreFallback", result.Message, null);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog(
                "Warning",
                "StartupRestoreFallback",
                LocalizationService.Instance.GetString("ProxyRecovery.StartupFailed"),
                exception.Message);
        }

        return Task.FromResult(StartupStepResult.ExitRequested());
    }
}
