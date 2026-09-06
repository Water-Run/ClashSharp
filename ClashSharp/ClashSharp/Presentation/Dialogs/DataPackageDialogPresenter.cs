using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Presentation.Composition;
using Microsoft.UI.Xaml;
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
            await _operations.ExportDataAsync(file.Path, selectedScope, cancellationToken);
        }
    }

    /// <summary>Imports a package after scope validation and two explicit overwrite confirmations.</summary>
    /// <returns>True only after the import transaction has completed successfully.</returns>
    public async Task<bool> ImportAsync(XamlRoot xamlRoot, CancellationToken cancellationToken)
    {
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
            throw new InvalidDataException("settings.data_package.invalid_scope");
        }

        if (!await ConfirmDataImportAsync(xamlRoot, scope!.Value, cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _operations.ImportDataPackageAsync(file.Path, cancellationToken);
        return true;
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
        List<DialogOptionRow> rows = [];
        (DataPackageExportScope Scope, string Key, string Glyph)[] options =
        [
            (DataPackageExportScope.Settings, "Settings", "\uE713"),
            (DataPackageExportScope.SettingsAndProxyConfiguration, "SettingsAndProxyConfiguration", "\uE968"),
            (DataPackageExportScope.SystemLogSqlite, "SystemLogSqlite", "\uE777"),
        ];
        foreach (var option in options)
        {
            DialogOptionRow row = new()
            {
                Title = _getString($"Settings.DataPackage.Scope.{option.Key}"),
                Metadata = _getString("Settings.DataExport.Title"),
                Description = _getString($"Settings.DataPackage.Scope.{option.Key}.Description"),
                Glyph = option.Glyph,
                IsChecked = option.Scope == DataPackageExportScope.Settings,
                Tag = option.Scope,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            row.SelectionInvoked += (_, _) => SelectDataPackageScopeRow(row, rows);
            rows.Add(row);
            panel.Children.Add(row);
        }

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

        return (DataPackageExportScope)rows.Single(static row => row.IsChecked).Tag;
    }

    private static void SelectDataPackageScopeRow(DialogOptionRow selectedRow, IReadOnlyList<DialogOptionRow> rows)
    {
        foreach (DialogOptionRow row in rows)
        {
            row.IsChecked = ReferenceEquals(row, selectedRow);
        }
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
