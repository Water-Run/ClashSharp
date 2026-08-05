using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>
/// Owns process-wide destructive mutation admission while settings and their external
/// runtime participants are committed or compensated as one operation.
/// </summary>
internal interface ISettingsDestructiveRuntimeScope : IAsyncDisposable
{
    /// <summary>Begins one retained package import using this scope's exclusive settings authority.</summary>
    Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(
        string packagePath,
        CancellationToken cancellationToken);

    /// <summary>Begins one retained full-settings reset using this scope's exclusive settings authority.</summary>
    ISettingsResetTransactionReceipt BeginResetSettings();

    /// <summary>Restores the durable participant-facing settings through this scope's exclusive authority.</summary>
    void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot);

    /// <summary>Applies launch registration without reacquiring ordinary mutation admission.</summary>
    Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken);

    /// <summary>Restarts connection sampling without reacquiring ordinary mutation admission.</summary>
    Task RestartConnectionSamplingAsync(CancellationToken cancellationToken);

    /// <summary>Applies TUN and mixed-port state through the already-admitted network transaction.</summary>
    Task ApplyNetworkSettingsAsync(
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken);
}

internal readonly record struct SettingsExternalDurableSnapshot(
    AppLanguage DisplayLanguage,
    AppThemeMode AppThemeMode,
    AppAccentColorMode AppAccentColorMode,
    string AppAccentColorValue,
    bool LaunchAtStartupEnabled,
    bool ConnectionSamplingEnabled,
    int ConnectionSamplingIntervalSeconds,
    ClashSharpMode CurrentMode,
    string ActiveProfileId,
    bool TransparentProxyEnabled,
    int MixedPort);

/// <summary>Retains the pre-import data generation until runtime activation chooses one final decision.</summary>
internal interface ISettingsDataPackageTransactionReceipt : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Retains the pre-reset settings generation until external participants either
/// converge to the defaults or require a durable rollback.
/// </summary>
internal interface ISettingsResetTransactionReceipt : IAsyncDisposable
{
    /// <summary>Commits the reset generation and discards its retained backup.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Restores the complete pre-reset settings generation.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
