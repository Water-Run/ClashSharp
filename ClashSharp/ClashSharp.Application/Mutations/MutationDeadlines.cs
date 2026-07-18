namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Defines bounded independent recovery deadlines.</summary>
/// <param name="StepTimeout">Maximum time for one recovery participant step.</param>
/// <param name="TotalRecoveryTimeout">Maximum time for a complete compensation or forward-recovery attempt.</param>
public sealed record MutationDeadlines(TimeSpan StepTimeout, TimeSpan TotalRecoveryTimeout)
{
    /// <summary>Gets the production recovery deadline policy.</summary>
    public static MutationDeadlines Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));

    internal void Validate()
    {
        if (StepTimeout <= TimeSpan.Zero || TotalRecoveryTimeout <= TimeSpan.Zero || StepTimeout > TotalRecoveryTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(StepTimeout), "Mutation deadlines must be positive and the step timeout cannot exceed the total timeout.");
        }
    }
}
