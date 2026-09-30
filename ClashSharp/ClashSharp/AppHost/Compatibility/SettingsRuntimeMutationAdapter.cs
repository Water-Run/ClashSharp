using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Adapts settings presentation actions to AppHost-owned mutation services.</summary>
internal sealed class SettingsRuntimeMutationAdapter
{
    private readonly IApplicationActionDispatcher _actions;
    private readonly ApplicationActionService _applicationActions;

    public SettingsRuntimeMutationAdapter(IApplicationActionDispatcher actions, ApplicationActionService applicationActions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _applicationActions = applicationActions ?? throw new ArgumentNullException(nameof(applicationActions));
    }

    /// <summary>Applies startup registration through the tracked application action boundary.</summary>
    public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken) =>
        _actions.DispatchAsync(ApplicationActionKind.SetLaunchAtStartup, isEnabled.ToString(), cancellationToken);

    /// <summary>Applies the complete page choice through the shared sampling transaction.</summary>
    public Task ApplyConnectionSamplingAsync(bool isEnabled, int intervalSeconds, CancellationToken cancellationToken) =>
        _applicationActions.ApplyConnectionSamplingSettingsAsync(isEnabled, intervalSeconds, cancellationToken);

    /// <summary>Applies requested TUN and mixed-port values as one verified runtime generation.</summary>
    public async Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken) =>
        _ = await _applicationActions.ApplyNetworkSettingsAsync(transparentProxyEnabled, mixedPort, cancellationToken).ConfigureAwait(false);
}
