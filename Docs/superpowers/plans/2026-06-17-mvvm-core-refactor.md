# Core MVVM Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build standard MVVM infrastructure and migrate `MainWindow`, `MasterControl`, `Proxies`, and `Settings` while preserving behavior.

**Architecture:** Add lightweight in-repository ViewModel base and command classes. Move service-driven state and commands from code-behind into bindable ViewModels backed by small service contracts and singleton adapters.

**Tech Stack:** .NET 10, C# 14, WinUI 3, xUnit, existing Clash# services.

---

## File Structure

- Create `ClashSharp/ClashSharp/ViewModel/ObservableObject.cs` for property notification.
- Create `ClashSharp/ClashSharp/ViewModel/RelayCommand.cs` for synchronous commands.
- Create `ClashSharp/ClashSharp/ViewModel/AsyncRelayCommand.cs` for asynchronous commands.
- Create `ClashSharp/ClashSharp/ViewModel/MainWindowViewModel.cs` for shell navigation labels and tag resolution.
- Create `ClashSharp/ClashSharp/ViewModel/MasterControlViewModel.cs` for master-mode commands and status state.
- Create `ClashSharp/ClashSharp/ViewModel/ProxiesViewModel.cs` for proxy node rows and latency command state.
- Modify `ClashSharp/ClashSharp/ViewModel/SettingsViewModel.cs` to inherit `ObservableObject` and expose bindable state.
- Modify `ClashSharp/ClashSharp/MainWindow.xaml` and `.xaml.cs` to bind navigation labels and delegate tag resolution.
- Modify `ClashSharp/ClashSharp/View/MasterControl.xaml` and `.xaml.cs` to bind text, status, and mode commands.
- Modify `ClashSharp/ClashSharp/View/Proxies.xaml` and `.xaml.cs` to bind text, command labels, nodes, and commands.
- Modify `ClashSharp/ClashSharp/View/Settings.xaml` and `.xaml.cs` to bind settings values and diagnostic status.
- Modify `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj` to link new ViewModel files.
- Create tests under `ClashSharp/ClashSharp.Tests/Unit/ViewModel/`.

## Tasks

### Task 1: MVVM Foundation

**Files:**
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/ObservableObjectTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/RelayCommandTests.cs`
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/AsyncRelayCommandTests.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/ObservableObject.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/RelayCommand.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/AsyncRelayCommand.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`

- [ ] Write failing tests proving `SetProperty` raises `PropertyChanged` only when values change.
- [ ] Write failing tests proving `RelayCommand` executes delegates and honors `CanExecute`.
- [ ] Write failing tests proving `AsyncRelayCommand` rejects reentrant execution and raises `CanExecuteChanged`.
- [ ] Run the three test files and verify they fail because the classes do not exist.
- [ ] Implement the three MVVM foundation files with full XML documentation and `#nullable enable`.
- [ ] Link the new files from the test project.
- [ ] Run the three test files and verify they pass.

### Task 2: Main Window ViewModel

**Files:**
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/MainWindowViewModelTests.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/MainWindowViewModel.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp/MainWindow.xaml`
- Modify: `ClashSharp/ClashSharp/MainWindow.xaml.cs`

- [ ] Write failing tests for navigation label loading and tag-to-page resolution.
- [ ] Run the tests and verify failure.
- [ ] Implement `MainWindowViewModel` with a localization contract and concrete adapter.
- [ ] Bind `NavigationViewItem.Content` values to ViewModel properties.
- [ ] Keep `Frame.Navigate` and Win32 title/min-size logic in code-behind.
- [ ] Run main-window tests and verify they pass.

### Task 3: Master Control ViewModel

**Files:**
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/MasterControlViewModelTests.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/MasterControlViewModel.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp/View/MasterControl.xaml`
- Modify: `ClashSharp/ClashSharp/View/MasterControl.xaml.cs`

- [ ] Write failing tests for initial localized labels, core status load, proxy status load, successful mode application, and failure status/logging.
- [ ] Run the tests and verify failure.
- [ ] Implement service contracts and singleton adapters inside `MasterControlViewModel.cs`.
- [ ] Implement bindable status properties and four mode commands.
- [ ] Bind page text, status cards, toggle checked states, and commands in XAML.
- [ ] Reduce code-behind to `InitializeComponent`, `DataContext`, and `Loaded` calling the ViewModel load command.
- [ ] Run master-control tests and verify they pass.

### Task 4: Proxies ViewModel

**Files:**
- Create: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/ProxiesViewModelTests.cs`
- Create: `ClashSharp/ClashSharp/ViewModel/ProxiesViewModel.cs`
- Modify: `ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj`
- Modify: `ClashSharp/ClashSharp/View/Proxies.xaml`
- Modify: `ClashSharp/ClashSharp/View/Proxies.xaml.cs`

- [ ] Write failing tests for localized labels, node refresh, successful latency test, and latency failure logging.
- [ ] Run the tests and verify failure.
- [ ] Implement proxy catalog, latency tester, log, and localization contracts.
- [ ] Implement bindable node collection and commands.
- [ ] Bind command labels, command objects, and list `ItemsSource`.
- [ ] Reduce code-behind to `InitializeComponent` and `DataContext`.
- [ ] Run proxies tests and verify they pass.

### Task 5: Settings ViewModel Binding Migration

**Files:**
- Modify: `ClashSharp/ClashSharp.Tests/Unit/ViewModel/SettingsViewModelTests.cs`
- Modify: `ClashSharp/ClashSharp/ViewModel/SettingsViewModel.cs`
- Modify: `ClashSharp/ClashSharp/View/Settings.xaml`
- Modify: `ClashSharp/ClashSharp/View/Settings.xaml.cs`

- [ ] Add failing tests proving settings properties raise changes and persist through setters.
- [ ] Add failing tests proving proxy information text refreshes after mixed port changes.
- [ ] Add failing tests proving diagnostic status properties update through command execution.
- [ ] Run settings tests and verify failure.
- [ ] Convert `SettingsViewModel` to inherit `ObservableObject`.
- [ ] Add bindable display text and setting properties while retaining existing validation behavior.
- [ ] Reuse `SettingsDiagnosticsViewModel.ExecuteCommandAsync` from async command wrappers.
- [ ] Bind settings controls using `Mode=TwoWay` where supported; keep minimal code-behind only if a WinUI control lacks reliable command binding.
- [ ] Run settings tests and verify they pass.

### Task 6: Verification And Architecture Audit

**Files:**
- Inspect: all modified files.

- [ ] Run `dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -p:Platform=x64`.
- [ ] Run `dotnet build ClashSharp/ClashSharp.slnx -p:Platform=x64`.
- [ ] Search core pages for direct singleton service calls and event handlers with `rg -n "Service\\.Instance|Click=|SelectionChanged=|Toggled=|ValueChanged=" ClashSharp/ClashSharp/MainWindow.xaml ClashSharp/ClashSharp/MainWindow.xaml.cs ClashSharp/ClashSharp/View/MasterControl.xaml ClashSharp/ClashSharp/View/MasterControl.xaml.cs ClashSharp/ClashSharp/View/Proxies.xaml ClashSharp/ClashSharp/View/Proxies.xaml.cs ClashSharp/ClashSharp/View/Settings.xaml ClashSharp/ClashSharp/View/Settings.xaml.cs`.
- [ ] Confirm remaining matches are WinUI-only lifecycle or explicitly deferred out of scope.
- [ ] Report exact verification results and any remaining deferred pages.
