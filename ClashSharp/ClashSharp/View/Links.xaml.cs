using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for managing subscription links used to update configuration profiles.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="LinksViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Creates input dialogs and delegates accepted input to the view model.
/// </remarks>
public sealed partial class Links : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly LinksViewModel _viewModel;

    private readonly PageLoadSession _loadSession = new();

    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private CancellationTokenSource _pageLifetime = new();

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal Links(LinksPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pageLifetime.IsCancellationRequested)
        {
            _pageLifetime = new CancellationTokenSource();
        }

        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
        _pageLifetime.Cancel();
    }

    /// <summary>Shows the add-link dialog and delegates accepted input to the view model.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AddLinkButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(async cancellationToken =>
        {
            TextBox nameBox = new()
            {
                Header = _getString("Links.Dialog.Name"),
                Text = _getString("Links.Dialog.DefaultName"),
            };
            TextBox uriBox = new()
            {
                Header = _getString("Links.Dialog.Uri"),
                PlaceholderText = "https://example.com/subscription.yaml",
            };
            StackPanel content = new()
            {
                Spacing = 12,
            };
            content.Children.Add(nameBox);
            content.Children.Add(uriBox);

            ThemedContentDialog dialog = new()
            {
                Title = _getString("Links.Dialog.AddTitle"),
                Content = content,
                PrimaryButtonText = _getString("Command.Add"),
                CloseButtonText = _getString("Command.Cancel"),
                XamlRoot = XamlRoot,
            };

            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary)
            {
                await _viewModel.AddLinkCommand.ExecuteObservedAsync(
                    (nameBox.Text, uriBox.Text),
                    cancellationToken);
            }
        });
    }

    private async void CheckLinksButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(
            cancellationToken => _viewModel.CheckLinkCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void UpdateLinksButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(
            cancellationToken => _viewModel.UpdateLinkCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void EditLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedLink is not ProfileSubscriptionLinkDisplay selectedLink)
        {
            return;
        }

        await RunPageOperationAsync(async cancellationToken =>
        {
            TextBox nameBox = new()
            {
                Header = _getString("Links.Dialog.Name"),
                Text = selectedLink.Model.Name,
            };
            TextBox uriBox = new()
            {
                Header = _getString("Links.Dialog.Uri"),
                Text = selectedLink.Model.Uri,
            };
            NumberBox intervalBox = new()
            {
                Header = _getString("Links.Dialog.UpdateIntervalHours"),
                Minimum = 1,
                Maximum = 8760,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Value = selectedLink.Model.UpdateIntervalHours,
            };
            ToggleSwitch enabledSwitch = new()
            {
                Header = _getString("Links.Dialog.AutomaticUpdates"),
                IsOn = selectedLink.Model.IsEnabled,
            };
            StackPanel content = new() { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(uriBox);
            content.Children.Add(intervalBox);
            content.Children.Add(enabledSwitch);

            ThemedContentDialog dialog = new()
            {
                Title = _getString("Links.Dialog.EditTitle"),
                Content = content,
                PrimaryButtonText = _getString("Command.Save"),
                CloseButtonText = _getString("Command.Cancel"),
                XamlRoot = XamlRoot,
            };
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary)
            {
                int updateIntervalHours = double.IsFinite(intervalBox.Value)
                    && intervalBox.Value is >= 1 and <= 8760
                    ? checked((int)intervalBox.Value)
                    : selectedLink.Model.UpdateIntervalHours;
                await _viewModel.EditLinkCommand.ExecuteObservedAsync(
                    new SubscriptionLinkEditRequest(
                        selectedLink.Model.Id,
                        nameBox.Text,
                        uriBox.Text,
                        enabledSwitch.IsOn,
                        updateIntervalHours),
                    cancellationToken);
            }
        });
    }

    private async void DeleteLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedLink is not ProfileSubscriptionLinkDisplay selectedLink)
        {
            return;
        }

        await RunPageOperationAsync(async cancellationToken =>
        {
            ThemedContentDialog dialog = new()
            {
                Title = _getString("Links.Dialog.DeleteTitle"),
                Content = selectedLink.Model.Name,
                PrimaryButtonText = _getString("Command.Delete"),
                CloseButtonText = _getString("Command.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            ContentDialogResult result = await dialog.ShowManagedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ContentDialogResult.Primary)
            {
                await _viewModel.DeleteLinkCommand.ExecuteObservedAsync(null, cancellationToken);
            }
        });
    }

    /// <summary>Queues durable page mutations without cancelling an older accepted operation.</summary>
    private async Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationToken cancellationToken = _pageLifetime.Token;
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            entered = true;
            SetOperationBusy(isBusy: true);
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (entered)
            {
                SetOperationBusy(isBusy: false);
                _operationGate.Release();
            }
        }
    }

    private void SetOperationBusy(bool isBusy)
    {
        AddLinkButton.IsEnabled = !isBusy;
        CheckLinksButton.IsEnabled = !isBusy;
        UpdateLinksButton.IsEnabled = !isBusy;
        EditLinkButton.IsEnabled = !isBusy;
        DeleteLinkButton.IsEnabled = !isBusy;
        SubscriptionLinksList.IsEnabled = !isBusy;
    }
}
