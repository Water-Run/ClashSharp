# Core MVVM Refactor Design

## Goal

Refactor the core Clash# WinUI surface to standard MVVM while preserving current behavior. The first implementation scope is `MainWindow`, `MasterControl`, `Proxies`, and `Settings`.

## Current Problems

The project has `Model`, `Service`, `View`, and `ViewModel` directories, but most core views still keep state transitions, service calls, localized text assignment, and command logic in code-behind. Existing ViewModels do not consistently implement bindable state, property change notifications, or command abstractions. Code-behind also directly calls singleton services, which makes behavior difficult to test without WinUI controls.

## Chosen Approach

Use a lightweight in-repository MVVM foundation instead of adding CommunityToolkit.Mvvm. This avoids generated-code friction with the repository's strict XML documentation policy and keeps the test project simple because it already links source files directly.

## Architecture

Add `ObservableObject`, `RelayCommand`, and `AsyncRelayCommand` under `ViewModel`. These classes provide `INotifyPropertyChanged`, synchronous command routing, asynchronous command routing, `CanExecute` updates, and busy-state protection.

Add testable service contracts beside the ViewModels that need them. Concrete adapters wrap existing singleton services. ViewModels depend on contracts, not WinUI controls. Views remain responsible only for WinUI-only tasks: `InitializeComponent`, `DataContext` assignment, window handle/title-bar setup, `Frame.Navigate`, and controls that cannot be practically represented as pure ViewModel operations.

`MainWindowViewModel` owns localized navigation labels, selected navigation tag, and tag-to-page resolution. It refreshes labels when localization changes. `MainWindow.xaml.cs` keeps native window sizing and title-bar interop, delegates navigation decisions to the ViewModel, and removes direct localization text assignment.

`MasterControlViewModel` owns mode selection, status labels, core version probing, proxy status refresh, and mode-application commands. It wraps the existing `MihomoCoreService`, `NetworkTakeoverService`, `WindowsProxyService`, `AppSettingsService`, and `LogStorageService` through small contracts.

`ProxiesViewModel` owns proxy node rows, refresh command, latency-test command, and busy state. It wraps `ProxyNodeCatalogService`, `ProxyLatencyService`, and `LogStorageService`.

`SettingsViewModel` becomes a real bindable ViewModel. It implements property change notification, exposes settings values as bindable properties, persists validated changes through `ISettingsStore`, exposes diagnostic command execution, and owns proxy information display. Existing diagnostics routing remains useful and is reused through command wrappers.

## Data Flow

User gestures bind to `ICommand` properties. Commands call ViewModel methods. ViewModel methods use service contracts and update bindable properties. XAML observes those properties through standard `{Binding}`. Code-behind does not mutate business state directly.

## Error Handling

The refactor preserves existing catch boundaries and log behavior. ViewModels convert expected service exceptions into status text and log entries. Unexpected exceptions are not swallowed. Async command cancellation uses `CancellationToken.None` for parity with current UI behavior, and service contracts keep cancellation parameters where the underlying service already supports them.

## Testing

Tests are added before production code. They cover property change notification, command execution, async busy-state behavior, main-window navigation mapping, master-control status transitions and failure behavior, proxies refresh and latency behavior, and settings persistence/binding behavior. Existing tests continue to run.

## Out Of Scope

`Profiles`, `Links`, `Logs`, `Rules`, `Statistics`, `Connections`, and `About` are not migrated in this pass except where shared infrastructure requires compilation changes. They remain candidates for later migration using the same pattern.
