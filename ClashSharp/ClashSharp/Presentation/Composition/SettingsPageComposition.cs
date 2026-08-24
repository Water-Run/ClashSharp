using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
        ApplicationActionService applicationActions = ApplicationActionService.Instance;
        ApplicationLifecycleService applicationLifecycle = ApplicationLifecycleService.Instance;
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
            applicationLifecycle.ExitApplication,
            applicationLifecycle.RestartApplication,
            () => startupRestoreFallback.GetStatus().IsRegistered,
            startupRestoreFallback.Register,
            startupRestoreFallback.RemoveRegistration,
            connectionProbe.GetStatusCodeAsync,
            diagnosticsViewModel,
            new MihomoServiceControllerAdapter(MihomoServiceManager.Instance),
            AppThemeService.ApplyAccentColor,
            clearAllDataAsync: applicationActions.ClearAllDataAndRestartAsync,
            checkStartupConflictsAsync: StartupConflictDetectionService.Instance.CheckConflictsAsync,
            isAccentColorRestartPending: AppThemeService.IsAccentColorRestartPending,
            notifyConnectionTestTimeout: NotificationService.Instance.NotifyConnectionTestTimeout,
            appendLog: logStorage.AppendLog,
            restartConnectionSamplingAsync: runtimeMutations.RestartConnectionSamplingAsync,
            applyLaunchAtStartupAsync: runtimeMutations.ApplyLaunchAtStartupAsync,
            supportedLanguages: LocalizationService.GetSupportedLanguages().ToArray(),
            applyNetworkSettingsAsync: runtimeMutations.ApplyNetworkSettingsAsync,
            requestResetRecoveryRestart: () =>
                applicationLifecycle.RequestRestart("settings-reset-recovery"),
            beginDestructiveRuntimeMutationAsync:
                runtimeMutations.BeginDestructiveMutationAsync);

        SettingsPageOperations operations = new(
            settings,
            localization,
            logStorage,
            ClashDataPackageService.Instance,
            runtimeMutations,
            applicationLifecycle,
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
    SettingsRuntimeMutationAdapter runtimeMutations,
    ApplicationLifecycleService applicationLifecycle,
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
        ExternalSettingsSnapshot baseline = CaptureExternalSettingsSnapshot();
        ISettingsDataPackageTransactionReceipt? receipt = null;
        bool activationCompleted = false;
        try
        {
            receipt = await runtimeMutation.BeginImportAsync(packagePath, cancellationToken)
                .ConfigureAwait(false);
            LegacyPageServiceBridge.Profiles.ResetAfterDataDeletion();
            ExternalSettingsSnapshot imported = CaptureExternalSettingsSnapshot();
            await ApplyExternalSettingsSnapshotAsync(imported, runtimeMutation)
                .ConfigureAwait(false);
            activationCompleted = true;
            await CompleteReceiptWithRetryAsync(
                receipt.CommitAsync,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception importActivationFailure)
            when (!ExceptionGraphClassifier.IsProcessFatal(importActivationFailure))
        {
            if (receipt is null || activationCompleted)
            {
                ExceptionDispatchInfo.Capture(importActivationFailure).Throw();
                throw;
            }

            Exception? rollbackFailure = null;
            try
            {
                await CompleteReceiptWithRetryAsync(
                    receipt.RollbackAsync,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                rollbackFailure = exception;
            }

            Exception? compensationFailure = null;
            if (rollbackFailure is null)
            {
                try
                {
                    LegacyPageServiceBridge.Profiles.ResetAfterDataDeletion();
                    await ApplyExternalSettingsSnapshotAsync(baseline, runtimeMutation)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    compensationFailure = exception;
                }
            }

            if (rollbackFailure is not null || compensationFailure is not null)
            {
                throw CreateImportRecoveryFailure(
                    importActivationFailure,
                    rollbackFailure,
                    compensationFailure);
            }

            ExceptionDispatchInfo.Capture(importActivationFailure).Throw();
            throw;
        }
        finally
        {
            if (receipt is not null)
            {
                await receipt.DisposeAsync().ConfigureAwait(false);
            }
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

    private ExternalSettingsSnapshot CaptureExternalSettingsSnapshot()
    {
        return new ExternalSettingsSnapshot(
            settings.DisplayLanguage,
            settings.AppThemeMode,
            settings.AppAccentColorMode,
            settings.AppAccentColorValue,
            settings.LaunchAtStartupEnabled,
            settings.ConnectionSamplingEnabled,
            settings.ConnectionSamplingIntervalSeconds,
            settings.CurrentMode,
            settings.ActiveProfileId,
            settings.TransparentProxyEnabled,
            settings.MixedPort);
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

    private async Task ApplyExternalSettingsSnapshotAsync(
        ExternalSettingsSnapshot snapshot,
        ISettingsDestructiveRuntimeScope runtimeMutation)
    {
        List<Exception> failures = [];
        CaptureFailure(() => localization.CurrentLanguage = snapshot.DisplayLanguage, failures);
        CaptureFailure(() => AppThemeService.Apply(snapshot.AppThemeMode), failures);
        CaptureFailure(
            () => AppThemeService.ApplyAccentColor(
                snapshot.AppAccentColorMode,
                snapshot.AppAccentColorValue),
            failures);
        await CaptureFailureAsync(
            () => runtimeMutation.ApplyLaunchAtStartupAsync(
                snapshot.LaunchAtStartupEnabled,
                CancellationToken.None),
            failures).ConfigureAwait(false);
        await CaptureFailureAsync(
            () => runtimeMutation.RestartConnectionSamplingAsync(CancellationToken.None),
            failures).ConfigureAwait(false);
        await CaptureFailureAsync(
            () => runtimeMutation.ApplyNetworkSettingsAsync(
                snapshot.TransparentProxyEnabled,
                snapshot.MixedPort,
                CancellationToken.None),
            failures).ConfigureAwait(false);
        CaptureFailure(
            () =>
            {
                if (CaptureExternalSettingsSnapshot() != snapshot)
                {
                    throw new InvalidOperationException(
                        "A settings import participant changed the durable imported generation.");
                }
            },
            failures);

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "One or more imported settings participants failed to converge.",
                failures);
        }
    }

    private static void CaptureFailure(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureFailureAsync(
        Func<Task> action,
        ICollection<Exception> failures)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private static async Task CompleteReceiptWithRetryAsync(
        Func<CancellationToken, Task> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception firstFailure) when (!ExceptionGraphClassifier.IsProcessFatal(firstFailure))
        {
            try
            {
                await completion(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception retryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(retryFailure))
            {
                throw new AggregateException(
                    "The retained data transaction completion could not be finalized after retry.",
                    firstFailure,
                    retryFailure);
            }
        }
    }

    private Exception CreateImportRecoveryFailure(
        Exception activationFailure,
        Exception? rollbackFailure,
        Exception? compensationFailure)
    {
        List<Exception> failures = [activationFailure];
        if (rollbackFailure is not null)
        {
            failures.Add(rollbackFailure);
        }

        if (compensationFailure is not null)
        {
            failures.Add(compensationFailure);
        }

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

    private readonly record struct ExternalSettingsSnapshot(
        AppLanguage DisplayLanguage,
        AppThemeMode AppThemeMode,
        AppAccentColorMode AppAccentColorMode,
        string AppAccentColorValue,
        bool LaunchAtStartupEnabled,
        bool ConnectionSamplingEnabled,
        int ConnectionSamplingIntervalSeconds,
        ClashSharpMode CurrentMode,
        string ActiveProfileId,
        bool TransparentProxyEnabled,
        int MixedPort);
}
