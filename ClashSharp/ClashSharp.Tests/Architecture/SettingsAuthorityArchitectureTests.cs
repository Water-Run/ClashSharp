namespace ClashSharp.Tests.Architecture;

/// <summary>
/// Guards the current production settings authority until the generation-backed cutover can be
/// performed as one migration, consumer, and repository-lifetime change.
/// </summary>
public sealed class SettingsAuthorityArchitectureTests
{
    private static readonly string ApplicationRoot = Path.Combine(
        FindRepositoryRoot(),
        "ClashSharp",
        "ClashSharp");

    /// <summary>
    /// Prevents a generation-backed envelope from becoming a shadow writer while synchronous
    /// LocalSettings consumers still treat <c>AppSettingsService</c> as the production authority.
    /// This guard must be replaced atomically by the eventual single-authority cutover tests.
    /// </summary>
    [Fact]
    public void ProductionApp_DoesNotActivateEnvelopeBesideLocalSettingsAuthority()
    {
        string settingsService = ReadApplicationSource("Service/AppSettingsService.cs");
        Assert.Contains(
            "ApplicationData.Current.LocalSettings",
            settingsService,
            StringComparison.Ordinal);

        string[] forbiddenEnvelopeActivationTokens =
        [
            "JsonSettingsRepository",
            "ISettingsRepository",
            "SettingsEnvelopeEditor",
            "DataGenerationManager",
            "FileDataGenerationStore",
        ];

        string[] offenders = EnumerateApplicationSources()
            .Where(path => forbiddenEnvelopeActivationTokens.Any(
                token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(ApplicationRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "The LocalSettings authority cannot be shadow-written to a settings envelope. "
            + "Complete migration, async change-set, consumer, and generation-lifetime cutover "
            + $"together. Envelope activation found in:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ControllerCredentials_AreOwnedSeparatelyFromPreferencesAndOpenedBeforeRuntimeRecovery()
    {
        string preferences = ReadApplicationSource("Service/AppSettingsService.cs")
            + ReadApplicationSource("Service/AppSettingsService.Mutations.cs");
        Assert.DoesNotContain("MihomoControllerSecret", preferences, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrCreateInternalSecret", preferences, StringComparison.Ordinal);
        Assert.DoesNotContain("RandomNumberGenerator", preferences, StringComparison.Ordinal);
        string configuration = ReadApplicationSource("Service/CoreConfigurationService.cs");
        Assert.Contains("IControllerCredentialProvider", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.MihomoControllerSecret", configuration, StringComparison.Ordinal);
        string host = ReadApplicationSource("AppHost/ClashSharpAppHostFactory.cs");
        Assert.Contains("AddSingleton<IControllerCredentialStore, WindowsControllerCredentialStore>()", host, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IStartupStep, ControllerCredentialStartupStep>()", host, StringComparison.Ordinal);
        string startup = ReadApplicationSource("AppHost/Startup/ControllerCredentialStartupStep.cs");
        Assert.Contains("Order => 140", startup, StringComparison.Ordinal);
        Assert.Contains("Order => 125", ReadApplicationSource("AppHost/Startup/InstallerTransactionStartupGate.cs"), StringComparison.Ordinal);
        Assert.Contains("Order => 150", ReadApplicationSource("AppHost/Startup/MutationRecoveryStartupStep.cs"), StringComparison.Ordinal);
        Assert.Contains("_credentials.ClearAll", ReadApplicationSource("Service/AppDataMaintenanceService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsImportAndReset_UseTheScopeOwnedExclusiveAuthority()
    {
        string pageComposition = ReadApplicationSource(
            "Presentation/Composition/SettingsPageComposition.cs");
        Assert.Contains("SettingsImportCoordinator", pageComposition, StringComparison.Ordinal);
        string importAdapter = ReadApplicationSource("Presentation/Adapters/SettingsImportOperationAdapter.cs");
        Assert.Contains("runtimeMutation.BeginImportAsync(packagePath, cancellationToken)", importAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt.CommitAsync", pageComposition, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt.RollbackAsync", pageComposition, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dataPackages.BeginImportAsync(packagePath, cancellationToken)",
            pageComposition,
            StringComparison.Ordinal);

        string settingsViewModel = ReadApplicationSource("ViewModel/SettingsViewModel.cs");
        Assert.Contains("SettingsResetCoordinator", settingsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("resetReceipt.CommitAsync", settingsViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("resetReceipt.RollbackAsync", settingsViewModel, StringComparison.Ordinal);
        string resetOperation = ReadApplicationSource("ViewModel/SettingsViewModel.ResetOperation.cs");
        Assert.Contains(
            "runtimeMutation.BeginResetSettings()",
            resetOperation,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtimeMutation.BeginResetStartupSettings()",
            resetOperation,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtimeMutation.BeginResetNetworkSettings(scope, transparentProxyEnabled)",
            resetOperation,
            StringComparison.Ordinal);

        string runtimeAdapter = ReadApplicationSource(
            "AppHost/Compatibility/SettingsRuntimeMutationAdapter.cs");
        Assert.Contains("BeginImportAdmittedAsync", runtimeAdapter, StringComparison.Ordinal);
        Assert.Contains("BeginResetSettingsAdmitted", runtimeAdapter, StringComparison.Ordinal);
        Assert.Contains("BeginResetStartupSettingsAdmitted", runtimeAdapter, StringComparison.Ordinal);
        Assert.Contains("BeginResetNetworkSettingsAdmitted", runtimeAdapter, StringComparison.Ordinal);
        Assert.Contains("WriteAdmitted(GetLease()", runtimeAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("AsyncLocal", runtimeAdapter, StringComparison.Ordinal);

        string dataPackages = ReadApplicationSource("Service/ClashDataPackageService.cs");
        string dataTransactions = ReadApplicationSource(
            "Service/ClashDataPackageService.Transaction.cs");
        Assert.Contains("#if UNIT_TESTS", dataPackages, StringComparison.Ordinal);
        Assert.Contains("#if UNIT_TESTS", dataTransactions, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDataRecovery_HoldsExclusiveAdmissionForAdmittedReplay()
    {
        string recoveryStep = ReadApplicationSource(
            "AppHost/Startup/DataPackageRecoveryStartupStep.cs");
        Assert.Contains("CloseAndDrainAsync", recoveryStep, StringComparison.Ordinal);
        Assert.Contains(
            "MutationAdmissionClosure.Destructive",
            recoveryStep,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReconcilePendingTransactionAdmittedAsync(recoveryLease",
            recoveryStep,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupChanges_ShareTheApplicationCoordinatorWithoutPagePersistence()
    {
        string host = ReadApplicationSource("AppHost/ClashSharpAppHostFactory.cs");
        Assert.Contains("AddSingleton<StartupSettingsCoordinator>()", host, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IStartupSettingsOperation, StartupSettingsOperationAdapter>()", host, StringComparison.Ordinal);
        string actions = ReadApplicationSource("Service/ApplicationActionService.cs");
        Assert.Contains("_startupSettings.ApplyAsync(isEnabled, cancellationToken)", actions, StringComparison.Ordinal);
        string triggers = ReadApplicationSource("Service/TriggerActionRuntimeAdapter.cs");
        Assert.Contains(".ApplyAdmittedAsync(RequireBoolean(action), admissionLease, cancellationToken)", triggers, StringComparison.Ordinal);
        string viewModel = ReadApplicationSource("ViewModel/SettingsViewModel.cs");
        int start = viewModel.IndexOf("public void SetLaunchAtStartupEnabled", StringComparison.Ordinal);
        int end = viewModel.IndexOf("public bool SetMixedPort", start, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.LaunchAtStartupEnabled =", viewModel[start..end], StringComparison.Ordinal);
        Assert.Contains("ReloadCommittedLaunchAtStartup", viewModel[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSettingsWriters_UseImmediateOrExplicitAdmission()
    {
        string settingsService = ReadApplicationSource("Service/AppSettingsService.cs");
        string settingsMutations = ReadApplicationSource(
            "Service/AppSettingsService.Mutations.cs");
        Assert.Contains("WriteOrdinary(editor =>", settingsService, StringComparison.Ordinal);
        Assert.Contains("admission.AcquireOrdinary()", settingsMutations, StringComparison.Ordinal);
        Assert.Contains("EnsureActiveLease(admissionLease)", settingsMutations, StringComparison.Ordinal);
        Assert.DoesNotContain("AsyncLocal", settingsMutations, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", settingsMutations, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", settingsMutations, StringComparison.Ordinal);

        string networkCommitter = ReadApplicationSource(
            "AppHost/Compatibility/LegacyNetworkStateCommitter.cs");
        string triggerRuntime = ReadApplicationSource("Service/TriggerActionRuntimeAdapter.cs");
        string applicationActions = ReadApplicationSource("Service/ApplicationActionService.cs");
        string profileCatalog = ReadApplicationSource("Service/ProfileCatalogService.cs");
        Assert.Contains("settings.WriteAdmitted", networkCommitter, StringComparison.Ordinal);
        Assert.Contains("_samplingSettings", triggerRuntime, StringComparison.Ordinal);
        Assert.Contains("_samplingSettings", applicationActions, StringComparison.Ordinal);
        Assert.Contains("_settings.WriteAdmitted", ReadApplicationSource("AppHost/Compatibility/ConnectionSamplingSettingsOperationAdapter.cs"), StringComparison.Ordinal);
        Assert.Contains("SetActiveProfileAdmitted", profileCatalog, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplingChanges_ShareCoordinatorAndKeepPageChoicesUnpersisted()
    {
        string host = ReadApplicationSource("AppHost/ClashSharpAppHostFactory.cs");
        Assert.Contains("AddSingleton<ConnectionSamplingSettingsCoordinator>()", host, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IConnectionSamplingSettingsOperation, ConnectionSamplingSettingsOperationAdapter>()", host, StringComparison.Ordinal);
        string page = ReadApplicationSource("ViewModel/SettingsViewModel.cs");
        Assert.DoesNotContain("_settings.ConnectionSamplingEnabled =", page, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.ConnectionSamplingIntervalSeconds =", page, StringComparison.Ordinal);
        Assert.Contains("_applyConnectionSamplingAsync(desired.Enabled, desired.IntervalSeconds, cancellationToken)", page, StringComparison.Ordinal);
        string trigger = ReadApplicationSource("Service/TriggerActionRuntimeAdapter.cs");
        Assert.Contains(".SetEnabledAdmittedAsync(RequireBoolean(action), admissionLease, cancellationToken)", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.ConnectionSamplingEnabled =", trigger, StringComparison.Ordinal);
        string adapter = ReadApplicationSource("AppHost/Compatibility/ConnectionSamplingSettingsOperationAdapter.cs");
        Assert.DoesNotContain(".StopAsync(", adapter, StringComparison.Ordinal);
        Assert.Contains("_sampling.QuiesceAsync", adapter, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateApplicationSources()
    {
        return Directory
            .EnumerateFiles(ApplicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(ApplicationRoot, path).Replace('\\', '/');
                return !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && !relative.StartsWith("obj/", StringComparison.Ordinal);
            });
    }

    private static string ReadApplicationSource(string relativePath)
    {
        string path = Path.Combine(
            ApplicationRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing application source: {relativePath}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClashSharp", "ClashSharp.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ClashSharp repository root.");
    }
}
