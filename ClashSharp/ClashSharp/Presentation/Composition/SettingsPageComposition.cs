using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
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
    ISettingsPageOperations Operations);

/// <summary>Owns settings operations that require file, service, or application-state access.</summary>
internal interface ISettingsPageOperations
{
    /// <summary>Reads the declared scope from a data package, returning null for an invalid package.</summary>
    ClashDataPackageScope? ReadPackageScope(string packagePath);

    /// <summary>Imports one validated data package.</summary>
    Task ImportDataPackageAsync(string packagePath, CancellationToken cancellationToken);

    /// <summary>Exports settings data or the diagnostic log database.</summary>
    Task ExportDataAsync(
        string destinationPath,
        DataPackageExportScope scope,
        CancellationToken cancellationToken);

    /// <summary>Re-applies presentation settings after a successful import.</summary>
    void ApplyImportedPresentationSettings();

    /// <summary>Reports an unexpected page-boundary failure through the application diagnostic sink.</summary>
    Task ReportUnexpectedErrorAsync(
        string operationName,
        Exception exception,
        CancellationToken cancellationToken);
}

/// <summary>Legacy composition boundary for the settings page.</summary>
/// <remarks>
/// Process-wide legacy services are adapted only here. The view receives explicit dependencies so
/// a host-owned page factory can replace this boundary without changing visual code or view-model
/// behavior.
/// </remarks>
internal static class SettingsPageComposition
{
    /// <summary>Creates one settings-page dependency graph from application-owned services.</summary>
    public static SettingsPageDependencies Create()
    {
        AppSettingsService settings = AppSettingsService.Instance;
        LocalizationService localization = LocalizationService.Instance;
        LogStorageService logStorage = LogStorageService.Instance;
        CoreConfigurationService coreConfiguration = CoreConfigurationService.Instance;
        MihomoCoreService mihomoCore = MihomoCoreService.Instance;
        IApplicationErrorSink errorSink = ApplicationErrorSink.CreateDefault();
        SettingsRuntimeMutationAdapter runtimeMutations = SettingsRuntimeMutationAdapter.CreateDefault();
        StartupRestoreFallbackService startupRestoreFallback = StartupRestoreFallbackService.Instance;
        HttpStatusProbe connectionProbe = new(TimeSpan.FromSeconds(4));
        SettingsDiagnosticsViewModel diagnosticsViewModel = new(
            new WindowsDiagnosticsClient(WindowsNetworkDiagnosticService.Instance),
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
            ApplicationLifecycleService.Instance.ExitApplication,
            ApplicationLifecycleService.Instance.RestartApplication,
            () => startupRestoreFallback.GetStatus().IsRegistered,
            startupRestoreFallback.Register,
            startupRestoreFallback.Uninstall,
            connectionProbe.GetStatusCodeAsync,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(MihomoServiceManager.Instance),
            AppThemeService.ApplyAccentColor,
            resetAllSettings: AppDataMaintenanceService.ResetAllSettings,
            clearAllDataAsync: AppDataMaintenanceService.ClearAllDataAsync,
            checkStartupConflictsAsync: StartupConflictDetectionService.Instance.CheckConflictsAsync,
            isAccentColorRestartPending: AppThemeService.IsAccentColorRestartPending,
            notifyConnectionTestTimeout: NotificationService.Instance.NotifyConnectionTestTimeout,
            appendLog: logStorage.AppendLog,
            restartConnectionSamplingAsync: runtimeMutations.RestartConnectionSamplingAsync,
            applyLaunchAtStartupAsync: runtimeMutations.ApplyLaunchAtStartupAsync,
            supportedLanguages: LocalizationService.GetSupportedLanguages().ToArray());

        SettingsPageOperations operations = new(
            settings,
            localization,
            logStorage,
            ClashDataPackageService.Instance,
            errorSink);

        return new SettingsPageDependencies(
            viewModel,
            localization.GetString,
            RestartRequiredStateService.Instance.SetRestartPending,
            AppThemeService.ParseAccentColorOrDefault,
            AppThemeService.FormatAccentColor,
            errorSink,
            StartupGuideComposition.Create(errorSink),
            operations);
    }
}

/// <summary>Default settings-page implementation for package, log-export, and error operations.</summary>
internal sealed class SettingsPageOperations(
    AppSettingsService settings,
    LocalizationService localization,
    LogStorageService logStorage,
    ClashDataPackageService dataPackages,
    IApplicationErrorSink errorSink) : ISettingsPageOperations
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
            string? scopeText = XDocument.Load(packagePath).Root?.Attribute("Scope")?.Value;
            return Enum.TryParse(scopeText, out ClashDataPackageScope scope)
                ? scope
                : null;
        }
        catch (Exception exception) when (ExceptionGraphClassifier.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task ImportDataPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        return dataPackages.ImportAsync(packagePath, cancellationToken);
    }

    /// <inheritdoc />
    public Task ExportDataAsync(
        string destinationPath,
        DataPackageExportScope scope,
        CancellationToken cancellationToken)
    {
        return scope switch
        {
            DataPackageExportScope.Settings => dataPackages.ExportAsync(
                destinationPath,
                ClashDataPackageScope.Settings,
                cancellationToken),
            DataPackageExportScope.SettingsAndProxyConfiguration => dataPackages.ExportAsync(
                destinationPath,
                ClashDataPackageScope.SettingsAndProxyConfiguration,
                cancellationToken),
            DataPackageExportScope.SystemLogSqlite => ExportLogDatabaseAsync(
                destinationPath,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported export scope."),
        };
    }

    /// <inheritdoc />
    public void ApplyImportedPresentationSettings()
    {
        localization.CurrentLanguage = settings.DisplayLanguage;
        AppThemeService.Apply(settings.AppThemeMode);
        AppThemeService.ApplyAccentColor(settings.AppAccentColorMode, settings.AppAccentColorValue);
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
