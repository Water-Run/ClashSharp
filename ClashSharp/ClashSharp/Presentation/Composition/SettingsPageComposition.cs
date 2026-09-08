using System;
using System.Collections.Generic;
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
    DataPackageDialogPresenter DataPackages);

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
        LogStorageService logStorage = context.LogStorage;
        CoreConfigurationService coreConfiguration = context.CoreConfiguration;
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
            () => startupRestoreFallback.GetStatus().IsRegistered,
            startupRestoreFallback.Register,
            startupRestoreFallback.RemoveRegistration,
            connectionProbe.GetStatusCodeAsync,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(context.MihomoService),
            AppThemeService.ApplyAccentColor,
            clearAllDataAsync: applicationActions.ClearAllDataAndRestartAsync,
            checkStartupConflictsAsync: context.StartupConflicts.CheckConflictsAsync,
            isAccentColorRestartPending: AppThemeService.IsAccentColorRestartPending,
            notifyConnectionTestTimeout: context.Notifications.NotifyConnectionTestTimeout,
            appendLog: logStorage.AppendLog,
            restartConnectionSamplingAsync: runtimeMutations.RestartConnectionSamplingAsync,
            applyLaunchAtStartupAsync: runtimeMutations.ApplyLaunchAtStartupAsync,
            supportedLanguages: LocalizationService.GetSupportedLanguages().ToArray(),
            applyNetworkSettingsAsync: runtimeMutations.ApplyNetworkSettingsAsync,
            requestResetRecoveryRestart: () =>
                applicationLifecycle.RequestRestart("settings-reset-recovery"),
            beginDestructiveRuntimeMutationAsync:
                runtimeMutations.BeginDestructiveMutationAsync);

        SettingsPageOperations operations = CreateOperations(context);

        return new SettingsPageDependencies(
            viewModel,
            localization.GetString,
            context.RestartState.SetRestartPending,
            AppThemeService.ParseAccentColorOrDefault,
            AppThemeService.FormatAccentColor,
            errorSink,
            context.StartupGuide.Create(errorSink),
            new DataPackageDialogPresenter(operations, localization.GetString));
    }

    /// <summary>Creates the shared transactional package port without constructing a settings view model.</summary>
    internal static SettingsPageOperations CreateOperations(PageCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SettingsPageOperations(
            context.Settings,
            context.Localization,
            context.LogStorage,
            context.DataPackages,
            context.SettingsRuntimeMutations,
            context.ApplicationLifecycle,
            context.Profiles,
            context.ErrorSink);
    }
}

/// <summary>Default settings-page implementation for package, log-export, and error operations.</summary>
internal sealed class SettingsPageOperations(
    AppSettingsService settings,
    LocalizationService localization,
    LogStorageService logStorage,
    ClashDataPackageService dataPackages,
    SettingsRuntimeMutationAdapter runtimeMutations,
    ApplicationLifecycleService applicationLifecycle,
    ProfileCatalogService profiles,
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
            string? scopeText = ClashDataPackageService
                .LoadBoundedPackage(packagePath)
                .Root?
                .Attribute("Scope")?
                .Value;
            return Enum.TryParse(scopeText, out ClashDataPackageScope scope)
                && Enum.IsDefined(scope)
                ? scope
                : null;
        }
        catch (Exception exception) when (ExceptionGraphClassifier.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task ImportDataPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        await using ISettingsDestructiveRuntimeScope runtimeMutation =
            await runtimeMutations.BeginDestructiveMutationAsync(cancellationToken);
        try
        {
            await new SettingsImportCoordinator().ExecuteAsync(
                new SettingsImportOperationAdapter(settings, localization, profiles, runtimeMutation),
                packagePath,
                cancellationToken);
        }
        catch (SettingsImportRecoveryException recoveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(recoveryFailure))
        {
            throw CreateImportRecoveryFailure(recoveryFailure.ActivationFailure, recoveryFailure.RecoveryFailure);
        }
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

    private Exception CreateImportRecoveryFailure(Exception activationFailure, Exception recoveryFailure)
    {
        List<Exception> failures = [activationFailure, recoveryFailure];

        try
        {
            if (!applicationLifecycle.RequestRestart("settings-import-recovery"))
            {
                failures.Add(new InvalidOperationException(
                    "The mandatory restart request was rejected after settings import recovery failed."));
            }
        }
        catch (Exception restartFailure) when (!ExceptionGraphClassifier.IsProcessFatal(restartFailure))
        {
            failures.Add(restartFailure);
        }

        return new AggregateException(
            "Settings import could not restore a consistent durable and external generation; restart recovery is required.",
            failures);
    }
}
