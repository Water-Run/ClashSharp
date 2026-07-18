using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Attaches the settings audit subscriber after recovery.</summary>
internal sealed class AppSettingsAuditStartupStep : IStartupStep
{
    public string Name => "settings-audit";

    public int Order => 400;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppSettingsAuditLogService.Instance.Start();
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
