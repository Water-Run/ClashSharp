namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>
/// Reports a disposal failure after host shutdown crossed its terminal ownership boundary.
/// </summary>
/// <remarks>
/// Retrying the same host is not supported after this exception. The outer process lifetime must
/// record the diagnostic and complete its terminal exit policy instead of re-entering host shutdown.
/// </remarks>
public sealed class ApplicationHostDisposalException : Exception
{
    /// <summary>Initializes a terminal host-disposal failure.</summary>
    /// <param name="innerException">Underlying provider or service disposal failure.</param>
    public ApplicationHostDisposalException(Exception innerException)
        : base(
            "Application host disposal failed after host ownership became terminal.",
            innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
    }
}
