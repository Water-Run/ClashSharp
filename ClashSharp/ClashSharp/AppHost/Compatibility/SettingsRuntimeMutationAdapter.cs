using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Bridges settings presentation actions to the AppHost-owned mutation dispatcher.</summary>
/// <remarks>This compatibility bridge is removed when settings pages are composed through dependency injection.</remarks>
internal sealed class SettingsRuntimeMutationAdapter
{
    private readonly IApplicationActionDispatcher _actions;

    private SettingsRuntimeMutationAdapter(IApplicationActionDispatcher actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <summary>Creates the temporary bridge at the presentation composition boundary.</summary>
    public static SettingsRuntimeMutationAdapter CreateDefault()
    {
        return new SettingsRuntimeMutationAdapter(ApplicationActionService.Instance);
    }

    /// <summary>Applies startup registration through the tracked application action boundary.</summary>
    public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        return _actions.DispatchAsync(
            ApplicationActionKind.SetLaunchAtStartup,
            isEnabled.ToString(),
            cancellationToken);
    }

    /// <summary>Restarts sampling from the latest persisted settings through the tracked action boundary.</summary>
    public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
    {
        return _actions.DispatchAsync(
            ApplicationActionKind.SetConnectionSampling,
            AppSettingsService.Instance.ConnectionSamplingEnabled.ToString(),
            cancellationToken);
    }
}
