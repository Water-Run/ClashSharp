using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Owns a complete installed network policy and independently observes the resulting runtime.</summary>
internal interface INetworkSettingsRuntime
{
    Task<NetworkSettingsConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken);
    Task RecoverConfigurationAsync(CancellationToken cancellationToken);
    Task ApplyConfigurationAsync(NetworkSettingsConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>Separates the installed TUN preference from its mode-dependent effective routing state.</summary>
internal sealed record NetworkSettingsConfiguration
{
    public NetworkSettingsConfiguration(ClashSharpMode mode, string profileId, bool transparentProxyEnabled, int mixedPort)
    {
        if (!Enum.IsDefined(mode) || mode == ClashSharpMode.Faulted) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(mixedPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mixedPort, 65535);
        SettingNormalizationResult normalized = SettingsRegistry.Default.Get(SettingsRegistry.Keys.ActiveProfileId.Value).NormalizeValue(profileId);
        if (!normalized.IsSuccess || !StringComparer.Ordinal.Equals(normalized.Value!.Get<string>(), profileId))
        {
            throw new ArgumentException("The runtime profile identity must be canonical.", nameof(profileId));
        }
        Mode = mode;
        ProfileId = profileId;
        TransparentProxyEnabled = transparentProxyEnabled;
        MixedPort = mixedPort;
    }

    public ClashSharpMode Mode { get; }
    public string ProfileId { get; }
    public bool TransparentProxyEnabled { get; }
    public int MixedPort { get; }
    public bool EffectiveTunEnabled => TransparentProxyEnabled && Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
}
