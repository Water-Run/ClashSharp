using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="MihomoServiceManager"/> to read-only service status for settings.</summary>
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
}
