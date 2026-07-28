using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Default test-friendly service controller used by legacy constructors.</summary>
internal sealed class AlwaysAvailableMihomoServiceController : IMihomoServiceController
{
    /// <summary>Shared controller instance.</summary>
    public static AlwaysAvailableMihomoServiceController Instance { get; } = new(key => key);

    private readonly Func<string, string> _getString;

    public AlwaysAvailableMihomoServiceController(Func<string, string> getString)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    public MihomoServiceStatus GetLatestStatus()
    {
        return new MihomoServiceStatus(true, false, _getString("MihomoService.Status.Deployed"));
    }

    public Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(GetLatestStatus());
    }

    public Task<MihomoServiceStatus> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetLatestStatus());
    }

    public Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new MihomoServiceStatus(false, false, _getString("MihomoService.Status.NotDeployed")));
    }
}
