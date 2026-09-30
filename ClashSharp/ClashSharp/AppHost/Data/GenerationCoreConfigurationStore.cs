using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Pins complete file and runtime transactions to the current generation; only pre-generation recovery may use the legacy root.</summary>
internal sealed class GenerationCoreConfigurationStore : ICoreConfigurationStore
{
    private readonly Func<DataGenerationManager> _getGenerations;
    private readonly Func<ICoreConfigurationStore>? _startupRecovery;

    public GenerationCoreConfigurationStore(DataGenerationManager generations) : this(() => generations) { }

    internal GenerationCoreConfigurationStore(Func<DataGenerationManager> getGenerations, Func<ICoreConfigurationStore>? startupRecovery = null)
    {
        _getGenerations = getGenerations ?? throw new ArgumentNullException(nameof(getGenerations));
        _startupRecovery = startupRecovery;
    }

    public CoreConfigurationState GetState() =>
        Execute(store => store.GetState());

    public CoreConfigurationState EnsureDefaultConfiguration() =>
        Execute(store => store.EnsureDefaultConfiguration());

    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode) =>
        Execute(store => store.EnsureConfiguration(mode));

    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled) =>
        Execute(store => store.EnsureConfiguration(mode, transparentProxyEnabled));

    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort) =>
        Execute(store => store.EnsureConfiguration(mode, transparentProxyEnabled, mixedPort));

    public Task<ProfileImportResult> ImportProfileConfigurationAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ImportProfileConfigurationAsync(profileId, profileName, configurationText, token), cancellationToken);

    public string GetProfileConfigurationPath(string profileId) =>
        Execute(store => store.GetProfileConfigurationPath(profileId));

    public bool TryReadProfileConfigurationText(string profileId, out string? configurationText)
    {
        var result = Execute(store => { bool found = store.TryReadProfileConfigurationText(profileId, out string? text); return (found, text); });
        configurationText = result.text;
        return result.found;
    }

    public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ValidateImportedProfileAsync(profileId, token), cancellationToken);

    public Task<RuntimeConfigurationGenerationState> GetRuntimeGenerationStateAsync(CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.GetRuntimeGenerationStateAsync(token), cancellationToken);

    public RuntimeConfigurationIntegrityObservation ObserveRuntimeConfigurationIntegrity() =>
        Execute(store => store.ObserveRuntimeConfigurationIntegrity());

    public bool CanRecoverInterruptedRuntimeConfiguration(RuntimeConfigurationActivationPlan baselinePlan, RuntimeConfigurationActivationPlan desiredPlan) =>
        Execute(store => store.CanRecoverInterruptedRuntimeConfiguration(baselinePlan, desiredPlan));

    public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ApplyRuntimeConfigurationAsync(mode, transparentProxyEnabled, mixedPort, runtime, token), cancellationToken);

    public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(string profileId, ClashSharpMode mode, bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ApplyRuntimeConfigurationAsync(profileId, mode, transparentProxyEnabled, mixedPort, runtime, token), cancellationToken);

    public Task<string?> ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ReadImportedProfileConfigurationAsync(profileId, token), cancellationToken);

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.DeleteImportedProfileAsync(profileId, token), cancellationToken);

    public Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(string profileId, string profileName, string configurationText, ClashSharpMode mode, bool effectiveTunEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken cancellationToken) =>
        ExecuteAsync((store, token) => store.ImportAndApplyProfileConfigurationAsync(profileId, profileName, configurationText, mode, effectiveTunEnabled, mixedPort, runtime, token), cancellationToken);

    private TResult Execute<TResult>(Func<ICoreConfigurationStore, TResult> operation)
    {
        DataGenerationManager generations = _getGenerations();
        if (generations.IsAwaitingInitialization && _startupRecovery is not null) { return operation(_startupRecovery()); }
        return generations.Execute<ICoreConfigurationStore, TResult>((store, _) => operation(store));
    }

    private Task<TResult> ExecuteAsync<TResult>(Func<ICoreConfigurationStore, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataGenerationManager generations = _getGenerations();
        if (generations.IsAwaitingInitialization && _startupRecovery is not null) { return operation(_startupRecovery(), cancellationToken); }
        return generations.ExecuteAsync<ICoreConfigurationStore, TResult>((store, _, token) => operation(store, token), cancellationToken);
    }
}
