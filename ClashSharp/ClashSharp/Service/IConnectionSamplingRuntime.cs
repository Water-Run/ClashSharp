using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;

namespace ClashSharp.Service;

/// <summary>Provides owned sampling operations without retaining a generation-specific sampler.</summary>
internal interface IConnectionSamplingRuntime : IRuntimeParticipant
{
    SupervisorHealth Health { get; }
    bool IsRunning { get; }
    Task RestartFromSettingsAsync(CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
    Task<ConnectionSamplingSettings> ReadConfigurationAsync(CancellationToken cancellationToken);
}
