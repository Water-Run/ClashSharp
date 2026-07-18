namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Arbitrates process ownership without constructing application services.</summary>
public interface IPrimaryInstanceBootstrap
{
    /// <summary>Acquires primary ownership or redirects the current activation.</summary>
    /// <param name="request">Current activation request.</param>
    /// <param name="cancellationToken">Cancels arbitration or redirection.</param>
    /// <returns>The ownership outcome.</returns>
    Task<PrimaryInstanceOwnership> AcquireAsync(AppLaunchRequest request, CancellationToken cancellationToken);
}
