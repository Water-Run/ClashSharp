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

        StorageFile? file = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is not null)
        {
            try
            {
                await _operations.ExportDataAsync(file.Path, selectedScope, cancellationToken);
            }
            catch (Exception exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception)
                && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
            {
                try
                {
                    await ShowExportResultAsync(xamlRoot, file.Path, succeeded: false, cancellationToken);
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
    }

    private async Task ShowExportResultAsync(
        XamlRoot xamlRoot,
        string destinationPath,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        ThemedContentDialog result = new()
        {
            Title = _getString(succeeded ? "Settings.DataExport.Completed" : "Settings.DataExport.Failed"),
            Content = new TextBlock
            {
                Text = succeeded
                    ? string.Format(CultureInfo.CurrentCulture, _getString("Settings.DataExport.SavedTo.Format"), destinationPath)
                    : _getString("Settings.DataExport.Failed.Description"),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = _getString("Command.Close"),
            XamlRoot = xamlRoot,
        };
        await result.ShowManagedAsync(cancellationToken);
    }

    /// <summary>Imports a package after scope validation and two explicit overwrite confirmations.</summary>
    /// <returns>True only after the import transaction has completed successfully.</returns>
    public async Task<bool> ImportAsync(XamlRoot xamlRoot,
        Func<CancellationToken, Task> refreshCommittedData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshCommittedData);
        cancellationToken.ThrowIfCancellationRequested();
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        InitializePickerWithWindow(picker);
        picker.FileTypeFilter.Add(".xml");
        StorageFile? file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return false;
        }

        ClashDataPackageScope? scope = _operations.ReadPackageScope(file.Path);
        if (!IsImportableDataPackageScope(scope))
        {
            await ShowImportResultAsync(xamlRoot,
                "Settings.DataImport.Invalid", "Settings.DataImport.Invalid.Description", cancellationToken);
            return false;
        }

        if (!await ConfirmDataImportAsync(xamlRoot, scope!.Value, cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SettingsDataReplacementResult result;
        try
        {
            result = await _operations.ImportDataPackageAsync(file.Path, cancellationToken);
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
        ThemedContentDialog result = new()
        {
            Title = _getString(titleKey),
            Content = new TextBlock { Text = _getString(messageKey), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = _getString("Command.Close"),
            XamlRoot = xamlRoot,
        };
        await result.ShowManagedAsync(cancellationToken);
    }

    private async Task<DataPackageExportScope?> SelectDataPackageExportScopeAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken)
    {
        StackPanel panel = new() { Spacing = 12, MinWidth = 320, MaxWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            Text = _getString("Settings.DataExport.Description"),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        RadioButtons scopeSelector = new() { MaxColumns = 1 };
        (DataPackageExportScope Scope, string Key)[] options =
        [
            (DataPackageExportScope.Settings, "Settings"),
            (DataPackageExportScope.SettingsAndProxyConfiguration, "SettingsAndProxyConfiguration"),
            (DataPackageExportScope.SystemLogSqlite, "SystemLogSqlite"),
        ];
        foreach (var option in options)
        {
            string title = _getString($"Settings.DataPackage.Scope.{option.Key}");
            string description = _getString($"Settings.DataPackage.Scope.{option.Key}.Description");
            StackPanel content = new() { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            content.Children.Add(new TextBlock
            {
                Text = title,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = description,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            RadioButton choice = new()
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(choice, title);
            AutomationProperties.SetHelpText(choice, description);
            scopeSelector.Items.Add(choice);
        }
        scopeSelector.SelectedIndex = 0;
        panel.Children.Add(scopeSelector);

        ThemedContentDialog dialog = new()
        {
            Title = _getString("Settings.DataExport.Title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(200, xamlRoot.Size.Height - 220) },
            PrimaryButtonText = _getString("Command.Export"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
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
        ThemedContentDialog firstDialog = new()
        {
            Title = _getString("Settings.DataImport.Warning.Title"),
            Content = FormatDataImportWarning(scope),
            PrimaryButtonText = _getString("Command.Import"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        if (await firstDialog.ShowManagedAsync(cancellationToken) is not ContentDialogResult.Primary)
        {
            return false;
        }

        ThemedContentDialog secondDialog = new()
        {
            Title = _getString("Settings.DataImport.SecondConfirm.Title"),
            Content = _getString("Settings.DataImport.SecondConfirm.Message"),
            PrimaryButtonText = _getString("Command.Import"),
            CloseButtonText = _getString("Command.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        return await secondDialog.ShowManagedAsync(cancellationToken) is ContentDialogResult.Primary;
    }

    private string FormatDataImportWarning(ClashDataPackageScope scope)
    {
        string scopeText = _getString($"Settings.DataPackage.Scope.{scope}");
        return $"{_getString("Settings.DataImport.Warning.Message")}{Environment.NewLine}"
            + string.Format(CultureInfo.CurrentCulture, _getString("Settings.DataImport.Warning.Scope.Format"), scopeText);
    }

    private static void InitializePickerWithWindow(object picker)
    {
        Window window = App.MainWindow
            ?? throw new InvalidOperationException("A file picker requires an active application window.");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }
}
