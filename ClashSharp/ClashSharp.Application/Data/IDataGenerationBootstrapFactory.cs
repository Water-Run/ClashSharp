using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Data;

/// <summary>Prepares complete, paused repository containers for first publication and subsequent startup.</summary>
public interface IDataGenerationBootstrapFactory
{
    /// <summary>Recovers retained legacy work, copies user data, and opens all repositories in a new first generation.</summary>
    /// <remarks>Must preserve legacy storage and must not publish a manifest or start runtime producers.</remarks>
    /// <param name="admissionLease">Exclusive startup ownership retained by the caller.</param>
    /// <param name="cancellationToken">Cancels preparation before manifest publication.</param>
    Task<DataGenerationScope> CreateInitialAsync(MutationAdmissionLease admissionLease, CancellationToken cancellationToken);

    /// <summary>Opens and verifies the complete repository container for an existing manifest without migrating legacy data.</summary>
    /// <param name="descriptor">Exact durable generation to open.</param>
    /// <param name="admissionLease">Exclusive startup ownership retained by the caller.</param>
    /// <param name="cancellationToken">Cancels opening repositories.</param>
    Task<DataGenerationScope> OpenAsync(
        DataGenerationDescriptor descriptor, MutationAdmissionLease admissionLease, CancellationToken cancellationToken);
}
