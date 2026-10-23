using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Shared, cancellable backup and restore presentation for settings and functional tiles.</summary>
/// <remarks>Pickers and confirmation dialogs only gather intent; injected operations own file and runtime transactions.</remarks>
internal sealed class DataPackageDialogPresenter
{
    private readonly ISettingsPageOperations _operations;
    private readonly Func<string, string> _getString;

    public DataPackageDialogPresenter(ISettingsPageOperations operations, Func<string, string> getString)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Shows the export scope and save picker, then awaits the selected export.</summary>
    public async Task ExportAsync(XamlRoot xamlRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataPackageExportScope? scope = await SelectDataPackageExportScopeAsync(xamlRoot, cancellationToken);
        if (scope is not DataPackageExportScope selectedScope)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        StorageFile? file = null;
        try
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"ClashSharp-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            InitializePickerWithWindow(picker);
            if (selectedScope == DataPackageExportScope.SystemLogSqlite)
            {
                picker.FileTypeChoices.Add("SQLite", [".sqlite3"]);
            }
            else
            {
                picker.FileTypeChoices.Add("Clash# XML", [".xml"]);
            }

            // The OS picker can fail before returning a file, for example when a selected backup is in use.
            file = await picker.PickSaveFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null)
            {
                return;
            }

            await _operations.ExportDataAsync(file.Path, selectedScope, cancellationToken);
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            try
            {
                await ShowExportResultAsync(xamlRoot, file?.Path ?? string.Empty, succeeded: false, cancellationToken);
            }
            finally
            {
                await _operations.ReportUnexpectedErrorAsync(
                    "settings-data-export", exception, CancellationToken.None);
            }
            return;
        }

        await ShowExportResultAsync(xamlRoot, file.Path, succeeded: true, cancellationToken);
    }

    private async Task ShowExportResultAsync(
        XamlRoot xamlRoot,
        string destinationPath,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        TextBlock message = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        ThemedContentDialog result = CreateDialog(xamlRoot);
        result.Content = message;
        result.Localize(() =>
        {
            result.Title = _getString(succeeded ? "Settings.DataExport.Completed" : "Settings.DataExport.Failed");
            message.Text = succeeded
                ? string.Format(CultureInfo.CurrentCulture, _getString("Settings.DataExport.SavedTo.Format"), destinationPath)
                : _getString("Settings.DataExport.Failed.Description");
            result.CloseButtonText = _getString("Command.Close");
        });
        await result.ShowManagedAsync(cancellationToken);
    }

    /// <summary>Imports a package after scope validation and two explicit overwrite confirmations.</summary>
    /// <returns>True only after the import transaction has completed successfully.</returns>
    public async Task<bool> ImportAsync(XamlRoot xamlRoot,
        Func<CancellationToken, Task> refreshCommittedData, CancellationToken cancellationToken,
        CancellationToken? completionCancellationToken = null)
    {
        ArgumentNullException.ThrowIfNull(refreshCommittedData);
        cancellationToken.ThrowIfCancellationRequested();
        IPreparedSettingsImport? prepared;
        try
        {
            FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            InitializePickerWithWindow(picker);
            picker.FileTypeFilter.Add(".xml");
            StorageFile? file = await picker.PickSingleFileAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null)
            {
                return false;
            }

            prepared = await _operations.PrepareDataImportAsync(file.Path, cancellationToken);
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            try
            {
                await ShowImportResultAsync(xamlRoot,
                    "Settings.DataImport.Failed", "Settings.DataImport.Failed.Description", cancellationToken);
            }
            finally
            {
                await _operations.ReportUnexpectedErrorAsync(
                    "settings-data-import", exception, CancellationToken.None);
            }
            return false;
        }

        using IPreparedSettingsImport? import = prepared;
        if (import is null || !IsImportableDataPackageScope(import.Scope))
        {
            await ShowImportResultAsync(xamlRoot,
                "Settings.DataImport.Invalid", "Settings.DataImport.Invalid.Description", cancellationToken);
            return false;
        }

        if (!await ConfirmDataImportAsync(xamlRoot, import.Scope, cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SettingsDataReplacementResult result;
        try
        {
            result = await _operations.ImportDataPackageAsync(import, cancellationToken);
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            try
            {
                bool recoveryRequired = exception is SettingsDataReplacementRecoveryException;
                await ShowImportResultAsync(xamlRoot,
                    recoveryRequired ? "Settings.DataReplacement.RecoveryRequired" : "Settings.DataImport.Failed",
                    recoveryRequired ? "Settings.DataReplacement.RecoveryRequired.Description" : "Settings.DataImport.Failed.Description",
                    cancellationToken);
            }
            finally
            {
                await _operations.ReportUnexpectedErrorAsync(
                    "settings-data-import", exception, CancellationToken.None);
            }
            return false;
        }

        // A committed replacement revokes old-data actions, including this import's input token.
        // Its confirmation remains owned by the same visible page; navigating away still cancels it.
        cancellationToken = completionCancellationToken ?? cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        // Publish page and tile state before presenting completion, while the page operation still owns input.
        await refreshCommittedData(cancellationToken);
        await ShowImportResultAsync(xamlRoot,
            "Settings.DataImport.Completed",
            result.RequiresRestart ? "Settings.DataReplacement.RestartRequired.Description"
                : result.Warnings.Count > 0 ? "Settings.DataReplacement.Warning.Description"
                : "Settings.DataImport.Completed.Description", cancellationToken);
        return true;
    }

    private async Task ShowImportResultAsync(
        XamlRoot xamlRoot, string titleKey, string messageKey, CancellationToken cancellationToken)
    {
        TextBlock message = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        ThemedContentDialog result = CreateDialog(xamlRoot);
        result.Content = message;
        result.Localize(() =>
        {
            result.Title = _getString(titleKey);
            message.Text = _getString(messageKey);
            result.CloseButtonText = _getString("Command.Close");
        });
        await result.ShowManagedAsync(cancellationToken);
    }

    private async Task<DataPackageExportScope?> SelectDataPackageExportScopeAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken)
    {
        ThemedContentDialog dialog = CreateDialog(xamlRoot);
        StackPanel panel = new() { Spacing = 12, Width = Math.Clamp(xamlRoot.Size.Width - 144, 320, 600) };
        TextBlock introduction = new()
        {
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        panel.Children.Add(introduction);
        RadioButtons scopeSelector = new() { MaxColumns = 1 };
        (DataPackageExportScope Scope, string Key)[] options =
        [
            (DataPackageExportScope.Settings, "Settings"),
            (DataPackageExportScope.SettingsAndProxyConfiguration, "SettingsAndProxyConfiguration"),
            (DataPackageExportScope.SystemLogSqlite, "SystemLogSqlite"),
        ];
        foreach (var option in options)
        {
            StackPanel content = new() { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            TextBlock title = new()
            {
                TextWrapping = TextWrapping.Wrap,
            };
            TextBlock description = new()
            {
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            };
            content.Children.Add(title);
            content.Children.Add(description);
            RadioButton choice = new()
            {
                Name = "DataPackageScope" + option.Key,
                Tag = option.Scope,
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            dialog.Localize(() =>
            {
                title.Text = _getString($"Settings.DataPackage.Scope.{option.Key}");
                description.Text = _getString($"Settings.DataPackage.Scope.{option.Key}.Description");
                AutomationProperties.SetName(choice, title.Text);
                AutomationProperties.SetHelpText(choice, description.Text);
            });
            scopeSelector.Items.Add(choice);
        }
        scopeSelector.SelectedIndex = 0;
        panel.Children.Add(scopeSelector);

        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(160, xamlRoot.Size.Height - 240) };
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Localize(() =>
        {
            dialog.Title = _getString("Settings.DataExport.Title");
            introduction.Text = _getString("Settings.DataExport.Description");
            dialog.PrimaryButtonText = _getString("Command.Export");
            dialog.CloseButtonText = _getString("Command.Cancel");
        });
        if (await dialog.ShowManagedAsync(cancellationToken) is not ContentDialogResult.Primary)
        {
            return null;
        }

        return scopeSelector.SelectedIndex >= 0
            ? options[scopeSelector.SelectedIndex].Scope
            : null;
    }

    private static bool IsImportableDataPackageScope(ClashDataPackageScope? scope) =>
        scope is ClashDataPackageScope.Settings or ClashDataPackageScope.SettingsAndProxyConfiguration;

    private async Task<bool> ConfirmDataImportAsync(
        XamlRoot xamlRoot,
        ClashDataPackageScope scope,
        CancellationToken cancellationToken)
    {
        ThemedContentDialog firstDialog = CreateDialog(xamlRoot);
        TextBlock warning = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        firstDialog.Content = warning;
        firstDialog.DefaultButton = ContentDialogButton.Close;
        firstDialog.Localize(() =>
        {
            firstDialog.Title = _getString("Settings.DataImport.Warning.Title");
            warning.Text = FormatDataImportWarning(scope);
            firstDialog.PrimaryButtonText = _getString("Command.Import");
            firstDialog.CloseButtonText = _getString("Command.Cancel");
        });
        if (await firstDialog.ShowManagedAsync(cancellationToken) is not ContentDialogResult.Primary)
        {
            return false;
        }

        ThemedContentDialog secondDialog = CreateDialog(xamlRoot);
        TextBlock confirmation = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        secondDialog.Content = confirmation;
        secondDialog.DefaultButton = ContentDialogButton.Close;
        secondDialog.Localize(() =>
        {
            secondDialog.Title = _getString("Settings.DataImport.SecondConfirm.Title");
            confirmation.Text = _getString("Settings.DataImport.SecondConfirm.Message");
            secondDialog.PrimaryButtonText = _getString("Command.Import");
            secondDialog.CloseButtonText = _getString("Command.Cancel");
        });
        return await secondDialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary;
    }

    private string FormatDataImportWarning(ClashDataPackageScope scope)
    {
        string scopeText = _getString($"Settings.DataPackage.Scope.{scope}");
        return $"{_getString("Settings.DataImport.Warning.Message")}{Environment.NewLine}"
            + string.Format(CultureInfo.CurrentCulture, _getString("Settings.DataImport.Warning.Scope.Format"), scopeText);
    }

    private static ThemedContentDialog CreateDialog(XamlRoot xamlRoot)
    {
        ThemedContentDialog dialog = new() { XamlRoot = xamlRoot };
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 688d;
        return dialog;
    }

    private static void InitializePickerWithWindow(object picker)
    {
        Window window = App.MainWindow
            ?? throw new InvalidOperationException("A file picker requires an active application window.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
