using System;
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

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the links page and its view model.</summary>
    public Links()
        : this(LinksPageComposition.Create())
    {
    }

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
        await _loadSession.RunAsync(_viewModel.LoadAsync);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
    }

    /// <summary>Shows the add-link dialog and delegates accepted input to the view model.</summary>
    /// <param name="sender">Command source. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void AddLinkButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(async cancellationToken =>
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
        await _loadSession.RunAsync(
            cancellationToken => _viewModel.CheckLinkCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }

    private async void UpdateLinksButton_Click(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(
            cancellationToken => _viewModel.UpdateLinkCommand.ExecuteObservedAsync(
                null,
                cancellationToken));
    }
}
