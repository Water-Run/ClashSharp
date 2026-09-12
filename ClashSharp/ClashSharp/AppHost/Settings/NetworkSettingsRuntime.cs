using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Settings;

/// <summary>Installs network preferences only after the real configuration, owner, controller and proxy agree.</summary>
internal sealed class NetworkSettingsRuntime : INetworkSettingsRuntime
{
    private readonly Func<CancellationToken, Task<NetworkSettingsConfiguration>> _observe;
    private readonly Func<NetworkSettingsConfiguration, CancellationToken, Task> _apply;
    private bool _inactiveTransparentProxyPolicy;
    private UnresolvedConfiguration? _unresolved;

    public NetworkSettingsRuntime(CoreConfigurationService configuration, NetworkTakeoverService takeover, WindowsProxyService proxy)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(proxy);
        _observe = token => takeover.ObserveNetworkSettingsAsync(configuration.ObserveRuntimeConfigurationIntegrity, proxy.ObserveOwnership, token);
        _apply = (target, token) => takeover.ApplyNetworkSettingsConfigurationAsync(configuration, target, token);
    }

    internal NetworkSettingsRuntime(Func<CancellationToken, Task<NetworkSettingsConfiguration>> observe,
        Func<NetworkSettingsConfiguration, CancellationToken, Task> apply)
    {
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public async Task<NetworkSettingsConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_unresolved is not null) { throw new InvalidOperationException("The network transaction requires runtime recovery before another settings attempt."); }
        NetworkSettingsConfiguration observed = await _observe(cancellationToken).ConfigureAwait(false);
        // Disabled and Standby deliberately have no effective TUN. The preference is an
        // installed strategy for the next takeover, published only after actual verification.
        // In a takeover mode the preference is reported from effective routing, never Desired.
        bool policy = observed.Mode is ClashSharpMode.Disabled or ClashSharpMode.Standby
            ? _inactiveTransparentProxyPolicy : observed.TransparentProxyEnabled;
        return new(observed.Mode, observed.ProfileId, policy, observed.MixedPort);
    }

    /// <summary>Allows an admitted explicit retry only after the entire previous baseline or target is observed.</summary>
    public async Task RecoverConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_unresolved is not UnresolvedConfiguration unresolved) { return; }
        NetworkSettingsConfiguration observed = await _observe(cancellationToken).ConfigureAwait(false);
        // Prefer the baseline when effective routing is identical: an inactive preference
        // has no external evidence until its installation transaction completes successfully.
        NetworkSettingsConfiguration recovered = Matches(unresolved.Before, observed) ? unresolved.Before
            : Matches(unresolved.Target, observed) ? unresolved.Target
            : throw new InvalidOperationException("Neither complete network baseline nor complete target has recovered.");
        _inactiveTransparentProxyPolicy = recovered.TransparentProxyEnabled;
        _unresolved = null;
    }

    public async Task ApplyConfigurationAsync(NetworkSettingsConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        NetworkSettingsConfiguration before = await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _apply(configuration, cancellationToken).ConfigureAwait(false);
            if (!Matches(configuration, await _observe(cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The requested network configuration could not be independently verified.");
            }
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            NetworkSettingsConfiguration? after = null;
            try { after = await _observe(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception probeFailure) when (!ExceptionGraphClassifier.IsProcessFatal(probeFailure)) { }
            if (after is null || !Matches(configuration, after))
            {
                // The existing runtime transaction owns compensation. A failed compensation
                // must not clear a partial settings batch merely because one scalar matches.
                _unresolved = after is null || !Matches(before, after) ? new(before, configuration) : null;
                throw;
            }
        }
        _inactiveTransparentProxyPolicy = configuration.TransparentProxyEnabled;
    }

    private static bool Matches(NetworkSettingsConfiguration expected, NetworkSettingsConfiguration observed) =>
        expected.Mode == observed.Mode && StringComparer.Ordinal.Equals(expected.ProfileId, observed.ProfileId)
        && expected.MixedPort == observed.MixedPort && expected.EffectiveTunEnabled == observed.TransparentProxyEnabled;

    private sealed record UnresolvedConfiguration(NetworkSettingsConfiguration Before, NetworkSettingsConfiguration Target);
}
