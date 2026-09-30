using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Infrastructure.Networking;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using Windows.UI;

namespace ClashSharp.Presentation.Composition;

/// <summary>Injected dependencies used by the settings view's platform-only interactions.</summary>
internal sealed record SettingsPageDependencies(
    SettingsViewModel ViewModel,
    Func<string, string> GetString,
    Action<bool> SetRestartPending,
    Func<string, Color> ParseAccentColor,
    Func<Color, string> FormatAccentColor,
    IApplicationErrorSink ErrorSink,
    IStartupGuidePresenter StartupGuide,
    DataPackageDialogPresenter DataPackages,
    Func<Action, IDisposable> SubscribeToRuntimeSettingsChanges);

/// <summary>Owns settings operations that require file, service, or application-state access.</summary>
internal interface ISettingsPageOperations
{
    /// <summary>Reads the declared scope from a data package, returning null for an invalid package.</summary>
    ClashDataPackageScope? ReadPackageScope(string packagePath);

    /// <summary>Imports one validated data package.</summary>
    Task<SettingsDataReplacementResult> ImportDataPackageAsync(string packagePath, CancellationToken cancellationToken);

    /// <summary>Exports settings data or the diagnostic log database.</summary>
    Task ExportDataAsync(
        string destinationPath,
        DataPackageExportScope scope,
        CancellationToken cancellationToken);

    /// <summary>Reports an unexpected page-boundary failure through the application diagnostic sink.</summary>
    Task ReportUnexpectedErrorAsync(
        string operationName,
        Exception exception,
        CancellationToken cancellationToken);
}

/// <summary>Builds the explicit dependency graph for the settings page.</summary>
internal static class SettingsPageComposition
{
    /// <summary>Creates one settings-page dependency graph from the AppHost-owned page context.</summary>
    public static SettingsPageDependencies Create(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AppSettingsService settings = context.Settings;
        LocalizationService localization = context.Localization;
        ILogStorage logStorage = context.LogStorage;
        ICoreConfigurationStore coreConfiguration = context.CoreConfiguration;
        MihomoCoreService mihomoCore = context.MihomoCore;
        ApplicationActionService applicationActions = context.ApplicationActions;
        ApplicationLifecycleService applicationLifecycle = context.ApplicationLifecycle;
        IApplicationErrorSink errorSink = context.ErrorSink;
        SettingsRuntimeMutationAdapter runtimeMutations = context.SettingsRuntimeMutations;
        StartupRestoreFallbackService startupRestoreFallback = context.StartupRestoreFallback;
        HttpStatusProbe connectionProbe = new(TimeSpan.FromSeconds(4));
        SettingsDiagnosticsViewModel diagnosticsViewModel = new(
            new WindowsDiagnosticsClient(context.WindowsDiagnostics),
            new DiagnosticsLog(logStorage),
            localization.GetString);
        SettingsViewModel viewModel = new(
            new AppSettingsStore(settings),
            language => localization.CurrentLanguage = language,
            AppThemeService.Apply,
            () => { },
            _ => { },
            localization.GetString,
            () =>
            {
                CoreConfigurationState configurationState = coreConfiguration.GetState();
                return new SettingsProxyInformation(
                    configurationState.ConfigPath,
                    mihomoCore.IsBinaryAvailable,
                    mihomoCore.BinaryPath);
            },
            errorSink,
            applicationLifecycle.ExitApplication,
            applicationLifecycle.RestartApplication,
            async token => (await startupRestoreFallback.GetStatusAsync(token)).IsRegistered,
            startupRestoreFallback.RegisterAsync,
            startupRestoreFallback.RemoveRegistrationAsync,
            connectionProbe.GetStatusCodeAsync,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(context.MihomoService),
            AppThemeService.ApplyAccentColor,
            clearAllDataAsync: applicationActions.ClearAllDataAndRestartAsync,
            checkStartupConflictsAsync: context.StartupConflicts.CheckConflictsAsync,
            isAccentColorRestartPending: AppThemeService.IsAccentColorRestartPending,
            notifyConnectionTestTimeout: context.Notifications.NotifyConnectionTestTimeout,
            appendLog: logStorage.AppendLog,
            applyConnectionSamplingAsync: runtimeMutations.ApplyConnectionSamplingAsync,
            applyLaunchAtStartupAsync: runtimeMutations.ApplyLaunchAtStartupAsync,
            supportedLanguages: LocalizationService.GetSupportedLanguages().ToArray(),
            applyNetworkSettingsAsync: runtimeMutations.ApplyNetworkSettingsAsync,
            requestResetRecoveryRestart: () =>
                applicationLifecycle.RequestRestart("settings-reset-recovery"),
            beginDestructiveRuntimeMutationAsync:
                runtimeMutations.BeginDestructiveMutationAsync,
            isDisplayLanguageRestartPending: language => language != localization.CurrentLanguage,
            replaceAllSettingsAsync: context.DataReplacement.ResetAllSettingsAsync);

        SettingsPageOperations operations = CreateOperations(context);

        return new SettingsPageDependencies(
            viewModel,
            localization.GetString,
            context.RestartState.SetRestartPending,
            AppThemeService.ParseAccentColorOrDefault,
            AppThemeService.FormatAccentColor,
            errorSink,
            context.StartupGuide.Create(errorSink),
            new DataPackageDialogPresenter(operations, localization.GetString),
            changed => new RuntimeSettingsSubscription(settings, changed));
    }

    private sealed class RuntimeSettingsSubscription : IDisposable
    {
        private AppSettingsService? _settings;
        private readonly Action _changed;

        public RuntimeSettingsSubscription(AppSettingsService settings, Action changed)
        {
            _settings = settings;
            _changed = changed;
            settings.SettingChanged += OnSettingChanged;
        }

        private void OnSettingChanged(object? sender, AppSettingChangedEventArgs args)
        {
            if (args.Key is nameof(AppSettingsService.LaunchAtStartupEnabled)
                or nameof(AppSettingsService.TransparentProxyEnabled)
                or nameof(AppSettingsService.MixedPort)
                or nameof(AppSettingsService.ConnectionSamplingEnabled)
                or nameof(AppSettingsService.ConnectionSamplingIntervalSeconds))
            {
                _changed();
            }
        }

        public void Dispose()
        {
            AppSettingsService? settings = Interlocked.Exchange(ref _settings, null);
            if (settings is not null)
            {
                settings.SettingChanged -= OnSettingChanged;
            }
        }
    }

    /// <summary>Creates the shared transactional package port without constructing a settings view model.</summary>
    internal static SettingsPageOperations CreateOperations(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SettingsPageOperations(
            context.LogStorage,
            context.DataPackages,
            context.DataReplacement,
            context.ErrorSink,
            context.SettingsExports);
    }
}

/// <summary>Default settings-page implementation for package, log-export, and error operations.</summary>
internal sealed class SettingsPageOperations(
    ILogStorage logStorage,
    IDataPackageExporter dataPackages,
    ISettingsDataReplacement dataReplacement,
    IApplicationErrorSink errorSink,
    SettingsExportCoordinator exports) : ISettingsPageOperations
{
    /// <inheritdoc />
    public ClashDataPackageScope? ReadPackageScope(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        try
        {
            return ClashDataPackageService.ReadImportScope(packagePath);
        }
        catch (Exception exception) when (ExceptionGraphClassifier.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<SettingsDataReplacementResult> ImportDataPackageAsync(string packagePath, CancellationToken cancellationToken) =>
        dataReplacement.ImportAsync(packagePath, cancellationToken);

    /// <inheritdoc />
    public Task ExportDataAsync(
        string destinationPath,
        DataPackageExportScope scope,
        CancellationToken cancellationToken)
    {
        return exports.ExecuteAsync((lease, token) => scope switch
        {
            DataPackageExportScope.Settings => dataPackages.ExportAdmittedAsync(
                destinationPath,
                ClashDataPackageScope.Settings,
                lease,
                token),
            DataPackageExportScope.SettingsAndProxyConfiguration => dataPackages.ExportAdmittedAsync(
                destinationPath,
                ClashDataPackageScope.SettingsAndProxyConfiguration,
                lease,
                token),
            DataPackageExportScope.SystemLogSqlite => ExportLogDatabaseAsync(
                destinationPath,
                token),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported export scope."),
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReportUnexpectedErrorAsync(
        string operationName,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return errorSink.ReportAsync(
            new ApplicationError(operationName, exception),
            cancellationToken);
    }

    private Task ExportLogDatabaseAsync(string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => logStorage.ExportDatabase(destinationPath),
            cancellationToken);
    }

}
