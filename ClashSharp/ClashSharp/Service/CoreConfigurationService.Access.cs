using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class CoreConfigurationService : ICoreConfigurationStore
{
    Task<RuntimeConfigurationGenerationState> ICoreConfigurationStore.GetRuntimeGenerationStateAsync(CancellationToken cancellationToken) => GetRuntimeGenerationStateAsync(cancellationToken);

    RuntimeConfigurationIntegrityObservation ICoreConfigurationStore.ObserveRuntimeConfigurationIntegrity() => ObserveRuntimeConfigurationIntegrity();

    bool ICoreConfigurationStore.CanRecoverInterruptedRuntimeConfiguration(RuntimeConfigurationActivationPlan baselinePlan, RuntimeConfigurationActivationPlan desiredPlan) => CanRecoverInterruptedRuntimeConfiguration(baselinePlan, desiredPlan);

    Task<RuntimeConfigurationTransactionResult> ICoreConfigurationStore.ApplyRuntimeConfigurationAsync(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => ApplyRuntimeConfigurationAsync(mode, transparentProxyEnabled, mixedPort, runtime, cancellationToken);

    Task<RuntimeConfigurationTransactionResult> ICoreConfigurationStore.ApplyRuntimeConfigurationAsync(string profileId, ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => ApplyRuntimeConfigurationAsync(profileId, mode, transparentProxyEnabled, mixedPort, runtime, cancellationToken);

    Task<string?> ICoreConfigurationStore.ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken) => ReadImportedProfileConfigurationAsync(profileId, cancellationToken);

    Task<bool> ICoreConfigurationStore.DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) => DeleteImportedProfileAsync(profileId, cancellationToken);

    Task<ProfileRuntimeConfigurationTransactionResult> ICoreConfigurationStore.ImportAndApplyProfileConfigurationAsync(string profileId, string profileName, string configurationText, ClashSharpMode mode, bool effectiveTunEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) => ImportAndApplyProfileConfigurationAsync(profileId, profileName, configurationText, mode, effectiveTunEnabled, mixedPort, runtime, cancellationToken);
}
