using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides complete configuration operations without exposing ownership of a data generation.</summary>
internal interface ICoreConfigurationStore
{
    CoreConfigurationState GetState();

    CoreConfigurationState EnsureDefaultConfiguration();

    CoreConfigurationState EnsureConfiguration(ClashSharpMode mode);

    CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled);

    CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort);

    Task<ProfileImportResult> ImportProfileConfigurationAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken);

    string GetProfileConfigurationPath(string profileId);

    bool TryReadProfileConfigurationText(string profileId, out string? configurationText);

    Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<RuntimeConfigurationGenerationState> GetRuntimeGenerationStateAsync(CancellationToken cancellationToken);

    RuntimeConfigurationIntegrityObservation ObserveRuntimeConfigurationIntegrity();

    bool CanRecoverInterruptedRuntimeConfiguration(RuntimeConfigurationActivationPlan baselinePlan, RuntimeConfigurationActivationPlan desiredPlan);

    Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken);

    Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(string profileId, ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken);

    Task<string?> ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken);

    Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(string profileId, string profileName, string configurationText, ClashSharpMode mode, bool effectiveTunEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken);
}
