/*
 * Settings Service Adapters
 * Connects settings view model service contracts to application services
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsServiceAdapters.cs
 * @date: 2026-06-24
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="MihomoServiceManager"/> to settings transparent proxy service controls.</summary>
internal sealed class MihomoServiceControllerAdapter : IMihomoServiceController
{
    /// <summary>Wrapped service manager.</summary>
    private readonly MihomoServiceManager _manager;

    /// <summary>Initializes the adapter.</summary>
    /// <param name="manager">Service manager. Must not be null.</param>
    public MihomoServiceControllerAdapter(MihomoServiceManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public MihomoServiceStatus GetLatestStatus()
    {
        return _manager.GetLatestStatus();
    }

    public Task<MihomoServiceStatus> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        return _manager.GetStatusAsync(cancellationToken);
    }

    public Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        return _manager.DeployAsync(cancellationToken);
    }

    public Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        return _manager.UninstallAsync(cancellationToken);
    }
}
