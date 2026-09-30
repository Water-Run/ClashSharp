using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Service;

public sealed partial class ConnectionSamplingService : IConnectionSamplingRuntime
{
    Task IConnectionSamplingRuntime.FlushAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);
    Task<ConnectionSamplingSettings> IConnectionSamplingRuntime.ReadConfigurationAsync(CancellationToken cancellationToken) =>
        ReadConfigurationAsync(cancellationToken);
}
