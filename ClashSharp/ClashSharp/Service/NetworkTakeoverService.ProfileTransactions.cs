using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    /// <summary>Applies one explicit profile through the current mode's sole runtime owner.</summary>
    internal async Task<RuntimeConfigurationTransactionResult> ApplyProfileConfigurationAsync(
        CoreConfigurationService configuration,
        string profileId,
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ValidateModeAndPort(mode, mixedPort);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool effectiveTunEnabled = await ResolveProfileTransactionTunAsync(
                mode,
                transparentProxyEnabled,
                cancellationToken).ConfigureAwait(false);
            return await configuration.ApplyRuntimeConfigurationAsync(
                profileId,
                mode,
                effectiveTunEnabled,
                mixedPort,
                this,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Imports an active profile candidate and applies it inside the same rollback boundary.</summary>
    internal async Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(
        CoreConfigurationService configuration,
        string profileId,
        string profileName,
        string configurationText,
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateModeAndPort(mode, mixedPort);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool effectiveTunEnabled = await ResolveProfileTransactionTunAsync(
                mode,
                transparentProxyEnabled,
                cancellationToken).ConfigureAwait(false);
            return await configuration.ImportAndApplyProfileConfigurationAsync(
                profileId,
                profileName,
                configurationText,
                mode,
                effectiveTunEnabled,
                mixedPort,
                this,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<bool> ResolveProfileTransactionTunAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        CancellationToken cancellationToken)
    {
        bool tunRequested = transparentProxyEnabled
            && mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
        if (!tunRequested)
        {
            return false;
        }

        MihomoServiceStatus serviceStatus = await _mihomoService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!serviceStatus.IsKnown)
        {
            throw new InvalidOperationException(
                "Mihomo service ownership cannot be planned because SCM status is unknown.");
        }

        return serviceStatus.IsInstalled;
    }
}
