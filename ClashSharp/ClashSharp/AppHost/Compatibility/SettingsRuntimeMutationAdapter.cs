using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Adapts settings presentation actions to AppHost-owned mutation services.</summary>
internal sealed class SettingsRuntimeMutationAdapter
{
    private readonly IApplicationActionDispatcher _actions;
    private readonly ApplicationActionService _applicationActions;
    private readonly AppSettingsService _settings;
    private readonly ClashDataPackageService _dataPackages;

    public SettingsRuntimeMutationAdapter(
        IApplicationActionDispatcher actions,
        ApplicationActionService applicationActions,
        AppSettingsService settings,
        ClashDataPackageService dataPackages)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _applicationActions = applicationActions
            ?? throw new ArgumentNullException(nameof(applicationActions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dataPackages = dataPackages ?? throw new ArgumentNullException(nameof(dataPackages));
    }

    /// <summary>Applies startup registration through the tracked application action boundary.</summary>
    public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        return _actions.DispatchAsync(
            ApplicationActionKind.SetLaunchAtStartup,
            isEnabled.ToString(),
            cancellationToken);
    }

    /// <summary>Restarts sampling from the latest persisted settings through the tracked action boundary.</summary>
    public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
    {
        return _actions.DispatchAsync(
            ApplicationActionKind.SetConnectionSampling,
            _settings.ConnectionSamplingEnabled.ToString(),
            cancellationToken);
    }

    /// <summary>Drains ordinary runtime mutations before a settings import or full reset commit point.</summary>
    public async ValueTask<ISettingsDestructiveRuntimeScope> BeginDestructiveMutationAsync(
        CancellationToken cancellationToken)
    {
        MutationAdmissionLease lease = await _applicationActions
            .BeginSettingsDestructiveMutationAsync(cancellationToken)
            .ConfigureAwait(false);
        return new DestructiveRuntimeScope(
            _applicationActions,
            _dataPackages,
            _settings,
            lease);
    }

    /// <summary>Applies requested TUN and mixed-port values as one verified runtime generation.</summary>
    public async Task ApplyNetworkSettingsAsync(
        bool transparentProxyEnabled,
        int mixedPort,
        CancellationToken cancellationToken)
    {
        _ = await _applicationActions
            .ApplyNetworkSettingsAsync(
                transparentProxyEnabled,
                mixedPort,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class DestructiveRuntimeScope(
        ApplicationActionService actions,
        ClashDataPackageService dataPackages,
        AppSettingsService settings,
        MutationAdmissionLease admissionLease) : ISettingsDestructiveRuntimeScope
    {
        private ApplicationActionService? _actions = actions;
        private readonly ClashDataPackageService _dataPackages = dataPackages;
        private readonly AppSettingsService _settings = settings;
        private MutationAdmissionLease? _admissionLease = admissionLease;

        public async Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            DataPackageTransactionReceipt receipt = await _dataPackages
                .BeginImportAdmittedAsync(packagePath, GetLease(), cancellationToken)
                .ConfigureAwait(false);
            return new DataTransactionReceipt(receipt);
        }

        public ISettingsResetTransactionReceipt BeginResetSettings()
        {
            return new ResetTransactionReceipt(
                _dataPackages.BeginResetSettingsAdmitted(GetLease()));
        }

        public void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot)
        {
            _settings.WriteAdmitted(GetLease(), editor =>
            {
                editor.DisplayLanguage = snapshot.DisplayLanguage;
                editor.AppThemeMode = snapshot.AppThemeMode;
                editor.AppAccentColorMode = snapshot.AppAccentColorMode;
                editor.AppAccentColorValue = snapshot.AppAccentColorValue;
                editor.LaunchAtStartupEnabled = snapshot.LaunchAtStartupEnabled;
                editor.ConnectionSamplingEnabled = snapshot.ConnectionSamplingEnabled;
                editor.ConnectionSamplingIntervalSeconds = snapshot.ConnectionSamplingIntervalSeconds;
                editor.CurrentMode = snapshot.CurrentMode;
                editor.ActiveProfileId = snapshot.ActiveProfileId;
                editor.TransparentProxyEnabled = snapshot.TransparentProxyEnabled;
                editor.MixedPort = snapshot.MixedPort;
            });
        }

        public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
        {
            return GetActions().ApplyLaunchAtStartupAdmittedAsync(isEnabled, cancellationToken);
        }

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
        {
            return GetActions().RestartConnectionSamplingAdmittedAsync(cancellationToken);
        }

        public async Task ApplyNetworkSettingsAsync(
            bool transparentProxyEnabled,
            int mixedPort,
            CancellationToken cancellationToken)
        {
            _ = await GetActions()
                .ApplyNetworkSettingsAdmittedAsync(
                    transparentProxyEnabled,
                    mixedPort,
                    GetLease(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _actions = null;
            MutationAdmissionLease? lease = Interlocked.Exchange(ref _admissionLease, null);
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        private ApplicationActionService GetActions()
        {
            return _actions ?? throw new ObjectDisposedException(nameof(DestructiveRuntimeScope));
        }

        private MutationAdmissionLease GetLease()
        {
            return _admissionLease ?? throw new ObjectDisposedException(nameof(DestructiveRuntimeScope));
        }
    }

    private sealed class DataTransactionReceipt(DataPackageTransactionReceipt receipt)
        : ISettingsDataPackageTransactionReceipt
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            return receipt.CommitAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            return receipt.RollbackAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return receipt.DisposeAsync();
        }
    }

    private sealed class ResetTransactionReceipt(DataPackageTransactionReceipt receipt)
        : ISettingsResetTransactionReceipt
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            return receipt.CommitAsync(cancellationToken);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            return receipt.RollbackAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return receipt.DisposeAsync();
        }
    }
}
