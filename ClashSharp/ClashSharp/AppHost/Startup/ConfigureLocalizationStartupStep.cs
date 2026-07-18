using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Startup;

/// <summary>Applies the persisted display language after primary ownership.</summary>
internal sealed class ConfigureLocalizationStartupStep : IStartupStep
{
    public string Name => "configure-localization";

    public int Order => 100;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalizationService.Instance.CurrentLanguage = AppSettingsService.Instance.DisplayLanguage;
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
