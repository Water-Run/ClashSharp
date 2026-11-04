using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.ViewModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.View;

/// <summary>Page for application identity, project links, and bundled mihomo core information.</summary>
/// <remarks>
/// Invariants: The page has a non-null <see cref="AboutViewModel"/> after construction.
/// Thread safety: Must be accessed from the UI thread only.
/// Side effects: Starts mihomo status loading when loaded and presents platform dialogs.
/// </remarks>
public sealed partial class About : Page
{
    /// <summary>Bindable view model for this page.</summary>
    private readonly AboutViewModel _viewModel;

    private readonly Func<string, string> _getString;

    private readonly Func<AboutPageComposition.ProxyInformation> _readProxyInformation;

    private readonly PageLoadSession _loadSession = new();

    /// <summary>Restores the copy button label after its transient confirmation; owned and stopped by this page.</summary>
    private DispatcherQueueTimer? _copyFeedbackTimer;

    /// <summary>Owns the in-flight copy so leaving the page cancels its clipboard retries.</summary>
    private CancellationTokenSource? _copyOperation;

    /// <summary>Initializes the page from an explicit composition contract.</summary>
    internal About(AboutPageComposition.Dependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _viewModel = dependencies.ViewModel;
        _getString = dependencies.GetString;
        _readProxyInformation = dependencies.ReadProxyInformation;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Starts mihomo status loading when the page enters the visual tree.</summary>
    /// <param name="sender">Loaded page instance. Not null.</param>
    /// <param name="e">Routed event arguments. Not null.</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _loadSession.RunAsync(async token =>
        {
            _viewModel.SetProxyPort(_readProxyInformation().MixedPort);
            await _viewModel.LoadAsync(token);
        });
    }

    /// <summary>Cancels update and core probes when the page leaves the visual tree.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadSession.Cancel();
        _copyOperation?.Cancel();
        _copyOperation?.Dispose();
        _copyOperation = null;
        CopyInformationButton.IsEnabled = true;
        _copyFeedbackTimer?.Stop();
        ShowCopyIdleState();
    }

    /// <summary>Copies non-sensitive application identity lines, then confirms the result on the button itself.</summary>
    private async void CopyInformationButton_Click(object sender, RoutedEventArgs e)
    {
        _copyOperation?.Cancel();
        _copyOperation?.Dispose();
        CancellationTokenSource operation = new();
        _copyOperation = operation;
        CopyInformationButton.IsEnabled = false;
        bool copied;
        try
        {
            copied = await ClipboardTextWriter.WriteAndVerifyAsync(_viewModel.CopyInformationText, isPrivate: false, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (COMException)
        {
            // The clipboard stayed unavailable for every bounded retry; the failure label is the complete result.
            copied = false;
        }
        finally
        {
            if (ReferenceEquals(_copyOperation, operation)) { CopyInformationButton.IsEnabled = true; }
        }

        CopyFeedbackGlyph.Glyph = copied ? "\uE73E" : "\uE783";
        CopyFeedbackText.Text = copied ? _viewModel.CopiedText : _viewModel.CopyFailedText;
        CopyIdleContent.Visibility = Visibility.Collapsed;
        CopyFeedbackContent.Visibility = Visibility.Visible;
        if (_copyFeedbackTimer is null)
        {
            _copyFeedbackTimer = DispatcherQueue.CreateTimer();
            _copyFeedbackTimer.Interval = TimeSpan.FromSeconds(2);
            _copyFeedbackTimer.IsRepeating = false;
            _copyFeedbackTimer.Tick += (_, _) => ShowCopyIdleState();
        }

        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void ShowCopyIdleState()
    {
        CopyFeedbackContent.Visibility = Visibility.Collapsed;
        CopyIdleContent.Visibility = Visibility.Visible;
    }

    /// <summary>Opens local proxy and core path information in a dialog.</summary>
    private async void OpenProxyInformationButton_Click(object sender, RoutedEventArgs e)
    {
        ThemedContentDialog dialog = new()
        {
            Title = _viewModel.ProxyInformationTitleText,
            CloseButtonText = _getString("Command.Close"),
            XamlRoot = XamlRoot,
        };
        dialog.Content = BuildProxyInformationPanel(dialog);
        dialog.Localize(() =>
        {
            dialog.Title = _viewModel.ProxyInformationTitleText;
            dialog.CloseButtonText = _getString("Command.Close");
        });

        await dialog.ShowManagedAsync();
    }

    /// <summary>Builds the proxy information dialog content.</summary>
    private StackPanel BuildProxyInformationPanel(ThemedContentDialog dialog)
    {
        AboutPageComposition.ProxyInformation information = _readProxyInformation();

        StackPanel panel = new()
        {
            Spacing = 10,
            MinWidth = 360,
            MaxWidth = 640,
        };

        AddInformationText(panel, dialog, () => string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.LocalEntry.Format"),
            information.MixedPort));
        AddInformationText(panel, dialog, () => string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.CoreConfig.Format"),
            information.ConfigPath));
        AddInformationText(panel, dialog, () => string.Format(
            CultureInfo.CurrentCulture,
            _getString("Settings.ProxyInformation.CoreBinary.Format"),
            information.IsCoreBinaryAvailable ? information.CoreBinaryPath : _getString("Settings.ProxyInformation.CoreBinary.Missing")));

        return panel;
    }

    /// <summary>Adds one wrapped information line to a panel.</summary>
    private static void AddInformationText(StackPanel panel, ThemedContentDialog dialog, Func<string> getText)
    {
        TextBlock line = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["ClashSecondaryBodyTextBlockStyle"],
            IsTextSelectionEnabled = true,
        };
        panel.Children.Add(line);
        dialog.Localize(() => line.Text = getText());
    }
}
