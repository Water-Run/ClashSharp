namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Coordinates host-owned shutdown work before service-provider disposal.</summary>
public interface IApplicationShutdownCoordinator
{
    /// <summary>Stops host-owned producers and operations.</summary>
    /// <param name="cancellationToken">Bounds shutdown.</param>
    Task StopAsync(CancellationToken cancellationToken);
}
