extern alias ClashSharpUi;
using System.ComponentModel;
using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using ClashSharpMode = global::ClashSharp.Model.ClashSharpMode;
using ITriggerPresentationSettings = ClashSharpUi::ClashSharp.ViewModel.ITriggerPresentationSettings;
using TriggerAction = global::ClashSharp.Model.Triggers.TriggerAction;
using TriggerActionEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerActionEditorViewModel;
using TriggerActionKind = global::ClashSharp.Model.Triggers.TriggerActionKind;
using TriggerCondition = global::ClashSharp.Model.Triggers.TriggerCondition;
using TriggerConditionEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerConditionEditorViewModel;
using TriggerConditionKind = global::ClashSharp.Model.Triggers.TriggerConditionKind;
using TriggerConditionTemplate = ClashSharpUi::ClashSharp.ViewModel.TriggerConditionTemplate;
using TriggerEditorSaveResult = ClashSharpUi::ClashSharp.ViewModel.TriggerEditorSaveResult;
using TriggerEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerEditorViewModel;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;
using TriggersViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggersViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies lossless multi-condition editing and asynchronous trigger-definition persistence.</summary>
public sealed class TriggerEditorViewModelTests
{
    [Fact]
    public async Task OpenEditSaveReload_RoundTripsEveryUntouchedConditionAndActionInOrder()
    {
        TriggerTaskDefinition original = CompleteDefinition("alpha", "Original");
        RecordingDefinitionStore store = new(Catalog(7, original));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));

        editor.Name = "Renamed";
        bool saved = await editor.SaveAsync(CancellationToken.None);

        Assert.True(saved);
        TriggerTaskDefinition persisted = Assert.Single(store.LastReplacement!);
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.Revision + 1, persisted.Revision);
        Assert.Equal("Renamed", persisted.Name);
        Assert.Equal(original.IsEnabled, persisted.IsEnabled);
        Assert.Equal(original.Conditions, persisted.Conditions);
        Assert.Equal(original.Actions, persisted.Actions);

        TriggersViewModel reloadedList = NewList(store);
        Assert.True(await reloadedList.LoadAsync(CancellationToken.None));
        TriggerEditorViewModel reloadedEditor = Assert.IsType<TriggerEditorViewModel>(
            reloadedList.BeginEdit("alpha"));
        Assert.True(reloadedEditor.TryBuildDefinition(out TriggerTaskDefinition? reloaded));
        Assert.NotNull(reloaded);
        Assert.Equal(persisted.Id, reloaded.Id);
        Assert.Equal(persisted.Revision, reloaded.Revision);
        Assert.Equal(persisted.Name, reloaded.Name);
        Assert.Equal(persisted.IsEnabled, reloaded.IsEnabled);
        Assert.Equal(persisted.Conditions, reloaded.Conditions);
        Assert.Equal(persisted.Actions, reloaded.Actions);
    }

    [Fact]
    public async Task OpenEditSave_InGermanCulturePreservesFractionalDurationTicks()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            TriggerTaskDefinition original = FractionalDurationDefinition();
            RecordingDefinitionStore store = new(Catalog(7, original));
            TriggersViewModel list = NewList(store);
            await list.LoadAsync(CancellationToken.None);
            TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(
                list.BeginEdit(original.Id));
            editor.Name = "Umbenannt";

            Assert.True(await editor.SaveAsync(CancellationToken.None));
            TriggerTaskDefinition persisted = Assert.Single(store.LastReplacement!);
            Assert.Equal(original.Conditions, persisted.Conditions);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task OpenEditSave_PreservesSemanticallyDuplicateConditionsWithDistinctIdentities()
    {
        TriggerTaskDefinition original = new(
            "duplicates",
            2,
            "Duplicates",
            true,
            [
                new TriggerCondition(
                    "first",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
                new TriggerCondition(
                    "second",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("done")),
            ]);
        RecordingDefinitionStore store = new(Catalog(3, original));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(
            list.BeginEdit(original.Id));
        editor.Name = "Renamed";

        Assert.True(await editor.SaveAsync(CancellationToken.None));
        TriggerTaskDefinition persisted = Assert.Single(store.LastReplacement!);
        Assert.Equal(["first", "second"], persisted.Conditions.Select(static condition => condition.Id));
        Assert.Equal(original.Conditions, persisted.Conditions);
    }

    [Fact]
    public async Task CatalogSummary_IncludesTheRollingWindowDuration()
    {
        RecordingDefinitionStore store = new(Catalog(7, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);

        Assert.Contains(
            TimeSpan.FromSeconds(90).ToString("g", CultureInfo.CurrentCulture),
            Assert.Single(list.TriggerTasks).ConditionsSummary,
            StringComparison.CurrentCulture);
    }

    [Fact]
    public void DraftCollections_AddRemoveAndReorderWithoutConstructingDomainObjectsInTheView()
    {
        TriggerEditorViewModel editor = NewEditor();
        TriggerConditionEditorViewModel first = editor.Conditions[0];
        TriggerConditionEditorViewModel traffic = editor.AddCondition(TriggerConditionTemplate.RollingTraffic);
        TriggerConditionEditorViewModel time = editor.AddCondition(TriggerConditionTemplate.SystemTime);
        time.TargetTimeText = "23:15";
        Assert.True(editor.MoveCondition(time, -1));
        Assert.True(editor.RemoveCondition(first));

        editor.Actions.Clear();
        TriggerActionEditorViewModel close = editor.AddAction(TriggerActionKind.CloseConnections);
        TriggerActionEditorViewModel notification = editor.AddAction(TriggerActionKind.SendNotification);
        notification.NotificationMessage = "finished";
        Assert.True(editor.MoveAction(notification, -1));

        Assert.True(editor.TryBuildDefinition(out TriggerTaskDefinition? definition));
        Assert.Equal([time.Id, traffic.Id], definition!.Conditions.Select(static condition => condition.Id));
        Assert.Equal(
            [TriggerActionKind.SendNotification, TriggerActionKind.CloseConnections],
            definition.Actions.Select(static action => action.Kind));
        Assert.Same(close, editor.Actions[1]);
    }

    [Fact]
    public void DuplicateConditionIdentity_IsRejectedBeforePersistence()
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Conditions.Add(editor.Conditions[0]);

        Assert.False(editor.TryBuildDefinition(out _));
        Assert.Equal("trigger.condition.id.duplicate", editor.ErrorCode);
    }

    [Theory]
    [InlineData("0", "300", TriggerTrafficScope.RollingWindow, "trigger.condition.threshold.invalid")]
    [InlineData("1024", "0", TriggerTrafficScope.RollingWindow, "trigger.condition.window.invalid")]
    [InlineData("1024", "300", (TriggerTrafficScope)999, "trigger.condition.traffic.scope.undefined")]
    public void InvalidTrafficThresholdWindowOrScope_IsRejected(
        string threshold,
        string windowSeconds,
        TriggerTrafficScope scope,
        string expectedCode)
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Conditions.Clear();
        TriggerConditionEditorViewModel condition = editor.AddCondition(TriggerConditionTemplate.RollingTraffic);
        condition.ThresholdText = threshold;
        condition.WindowSecondsText = windowSeconds;
        condition.TrafficScope = scope;

        Assert.False(editor.TryBuildDefinition(out _));
        Assert.Equal(expectedCode, editor.ErrorCode);
    }

    [Fact]
    public void InvalidSystemTime_IsRejectedBeforePersistence()
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Conditions.Clear();
        editor.AddCondition(TriggerConditionTemplate.SystemTime).TargetTimeText = "25:90";

        Assert.False(editor.TryBuildDefinition(out _));
        Assert.Equal("trigger.condition.time.invalid", editor.ErrorCode);
    }

    [Fact]
    public void NumericValidation_UsesTheFieldSpecificLocalizedMessage()
    {
        TriggerConditionEditorViewModel connections = TriggerConditionEditorViewModel.Create(
            TriggerConditionTemplate.ActiveConnections,
            Localize);
        connections.ThresholdText = "0";
        TriggerConditionEditorViewModel runtime = TriggerConditionEditorViewModel.Create(
            TriggerConditionTemplate.Runtime,
            Localize);
        runtime.RuntimeSecondsText = "0";

        Assert.False(connections.TryBuild(out _));
        Assert.Equal("Triggers.Validation.PositiveCount", connections.ErrorMessage);
        Assert.False(runtime.TryBuild(out _));
        Assert.Equal("Triggers.Validation.PositiveRuntime", runtime.ErrorMessage);
    }

    [Fact]
    public void InvalidNonSelectedCondition_IsSelectedAndShowsItsFieldSpecificError()
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Conditions.Clear();
        TriggerConditionEditorViewModel invalid = editor.AddCondition(
            TriggerConditionTemplate.ActiveConnections);
        invalid.ThresholdText = "0";
        editor.AddCondition(TriggerConditionTemplate.Runtime).RuntimeSecondsText = "60";

        Assert.False(editor.TryBuildDefinition(out _));
        Assert.Same(invalid, editor.SelectedCondition);
        Assert.Equal("Triggers.Validation.PositiveCount", invalid.ErrorMessage);
    }

    [Fact]
    public void InvalidNonSelectedAction_IsSelectedAndShowsItsFieldSpecificError()
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Actions.Clear();
        TriggerActionEditorViewModel invalid = editor.AddAction(
            TriggerActionKind.SendNotification);
        invalid.NotificationMessage = " ";
        editor.AddAction(TriggerActionKind.CloseConnections);

        Assert.False(editor.TryBuildDefinition(out _));
        Assert.Same(invalid, editor.SelectedAction);
        Assert.Equal("Triggers.Validation.NotificationMessageRequired", invalid.ErrorMessage);
    }

    [Fact]
    public async Task DuplicateName_IsRejectedWithoutCallingTheStore()
    {
        TriggerTaskDefinition first = CompleteDefinition("alpha", "Alpha");
        TriggerTaskDefinition second = CompleteDefinition("beta", "Beta");
        RecordingDefinitionStore store = new(Catalog(2, first, second));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));
        editor.Name = " beta ";

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("trigger.editor.name_duplicate", editor.ErrorCode);
        Assert.Equal(0, store.ReplaceCallCount);
    }

    [Fact]
    public async Task Save_ExposesBusyAndTypedPersistenceFailureState()
    {
        TaskCompletionSource<TriggerEditorSaveResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TriggerEditorViewModel editor = new(
            Localize,
            original: null,
            existingNames: [],
            (_, _) => completion.Task,
            new TestApplicationErrorSink(),
            newId: "new-task");

        Task<bool> firstSave = editor.SaveAsync(CancellationToken.None);
        Assert.True(editor.IsBusy);
        Assert.False(editor.CanEdit);
        Assert.False(editor.CanCancel);
        Assert.False(editor.CanSave);
        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("trigger.editor.busy", editor.ErrorCode);
        completion.SetResult(TriggerEditorSaveResult.Failed("trigger.repository.unavailable"));

        Assert.False(await firstSave);
        Assert.False(editor.IsBusy);
        Assert.True(editor.CanEdit);
        Assert.True(editor.CanCancel);
        Assert.True(editor.CanSave);
        Assert.Equal("trigger.repository.unavailable", editor.ErrorCode);
    }

    [Fact]
    public async Task CancelEdit_DuringSaveRetainsTheEditorUntilPersistenceCompletes()
    {
        TaskCompletionSource<TriggerPersistenceResult<TriggerDefinitionCatalog>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TriggerTaskDefinition original = CompleteDefinition("alpha", "Alpha");
        RecordingDefinitionStore store = new(Catalog(3, original))
        {
            ReplaceCompletion = completion,
        };
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));
        editor.Name = "Changed";

        Task<bool> save = editor.SaveAsync(CancellationToken.None);
        Assert.True(editor.IsBusy);
        list.CancelEdit();

        Assert.Same(editor, list.CurrentEditor);
        completion.SetResult(TriggerPersistenceResult.Succeeded(Catalog(
            4,
            CompleteDefinition("alpha", "Changed"))));
        Assert.True(await save);
        Assert.Null(list.CurrentEditor);
    }

    [Fact]
    public void ExitAction_RemainsFinalAcrossAddAndMoveOperations()
    {
        TriggerEditorViewModel editor = NewEditor();
        editor.Actions.Clear();
        TriggerActionEditorViewModel exit = editor.AddAction(TriggerActionKind.ExitApplication);
        TriggerActionEditorViewModel notification = editor.AddAction(TriggerActionKind.SendNotification);
        notification.NotificationMessage = "before exit";

        Assert.Equal([notification, exit], editor.Actions);
        Assert.False(editor.MoveAction(exit, -1));
        Assert.Equal("trigger.action.exit.must_be_final", editor.ErrorCode);
        Assert.True(editor.TryBuildDefinition(out TriggerTaskDefinition? definition));
        Assert.Equal(TriggerActionKind.ExitApplication, definition!.Actions[^1].Kind);
    }

    [Fact]
    public async Task BulkAndSingleEnablement_NoOpDoesNotAdvanceRepositoryGeneration()
    {
        TriggerTaskDefinition definition = CompleteDefinition("alpha", "Alpha");
        RecordingDefinitionStore store = new(Catalog(3, definition));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);

        Assert.True(await list.SetAllTasksEnabledAsync(true, CancellationToken.None));
        Assert.True(await list.SetTaskEnabledAsync("alpha", true, CancellationToken.None));
        Assert.Equal(0, store.ReplaceCallCount);
        Assert.Equal(3, store.Current.Generation);
    }

    [Fact]
    public async Task FailedSingleEnablement_RepublishesThePersistedValueForOneWayBinding()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")))
        {
            ReplaceException = new InvalidOperationException("storage failed"),
        };
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        var item = Assert.Single(list.TriggerTasks);
        INotifyPropertyChanged notifier = Assert.IsAssignableFrom<INotifyPropertyChanged>(item);
        List<string?> changedProperties = [];
        notifier.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.False(await list.SetTaskEnabledAsync("alpha", false, CancellationToken.None));
        Assert.True(item.IsEnabled);
        Assert.Contains(nameof(item.IsEnabled), changedProperties);
    }

    [Fact]
    public async Task MissingTaskEnablement_IsRejectedWithoutWritingTheCatalog()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);

        Assert.False(await list.SetTaskEnabledAsync("missing", false, CancellationToken.None));
        Assert.Equal("trigger.definition.not_found", list.ErrorCode);
        Assert.Equal(0, store.ReplaceCallCount);
    }

    [Fact]
    public async Task MissingTaskDeletion_IsRejectedWithoutWritingTheCatalog()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);

        Assert.False(await list.DeleteTaskAsync("missing", CancellationToken.None));
        Assert.Equal("trigger.definition.not_found", list.ErrorCode);
        Assert.Equal(0, store.ReplaceCallCount);
    }

    [Fact]
    public async Task UnexpectedStoreFailure_IsExposedAsTypedEditorError()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")))
        {
            ReplaceException = new InvalidOperationException("storage failed"),
        };
        TestApplicationErrorSink errorSink = new();
        TriggersViewModel list = NewList(store, errorSink);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));
        editor.Name = "Changed";

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("trigger.definition.write_unavailable", editor.ErrorCode);
        Assert.False(editor.IsBusy);
        Assert.False(list.IsBusy);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("Triggers.Replace", error.OperationName);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task UnexpectedReadFailure_IsExposedAsTypedListError()
    {
        RecordingDefinitionStore store = new(Catalog(0))
        {
            ReadException = new InvalidOperationException("storage failed"),
        };
        TestApplicationErrorSink errorSink = new();
        TriggersViewModel list = NewList(store, errorSink);

        Assert.False(await list.LoadAsync(CancellationToken.None));
        Assert.Equal("trigger.definition.read_unavailable", list.ErrorCode);
        Assert.Equal("Triggers.Validation.LoadFailed", list.ErrorMessage);
        Assert.False(list.IsBusy);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("Triggers.Load", error.OperationName);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task ProcessFatalReadFailure_PropagatesWithoutDiagnosticContainment()
    {
        RecordingDefinitionStore store = new(Catalog(0))
        {
            ReadException = CreateProcessFatalException<OutOfMemoryException>(),
        };
        TestApplicationErrorSink errorSink = new();
        TriggersViewModel list = NewList(store, errorSink);

        await Assert.ThrowsAsync<OutOfMemoryException>(
            () => list.LoadAsync(CancellationToken.None));

        Assert.False(list.IsBusy);
        Assert.Empty(errorSink.Errors);
    }

    [Fact]
    public async Task TypedStorageReadFailure_UsesLoadFailurePresentation()
    {
        RecordingDefinitionStore store = new(Catalog(0))
        {
            ReadResult = TriggerPersistenceResult.Unavailable<TriggerDefinitionCatalog>(
                new TriggerDiagnostic(
                    "trigger.storage.read_failed",
                    TriggerDiagnosticSeverity.Error,
                    null,
                    "read_snapshot",
                    DateTimeOffset.UnixEpoch)),
        };
        TriggersViewModel list = NewList(store);

        Assert.False(await list.LoadAsync(CancellationToken.None));
        Assert.Equal("trigger.storage.read_failed", list.ErrorCode);
        Assert.Equal("Triggers.Validation.LoadFailed", list.ErrorMessage);
        Assert.False(list.IsBusy);
    }

    [Fact]
    public async Task LoadCancellation_PropagatesWithoutLeavingTheListBusy()
    {
        RecordingDefinitionStore store = new(Catalog(0));
        TriggersViewModel list = NewList(store);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => list.LoadAsync(cancellation.Token));

        Assert.False(list.IsBusy);
        Assert.Null(list.ErrorCode);
    }

    [Fact]
    public async Task SaveCancellation_PropagatesAndRestoresEditorAvailability()
    {
        TriggerEditorViewModel editor = new(
            Localize,
            original: null,
            existingNames: [],
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return TriggerEditorSaveResult.Succeeded();
            },
            new TestApplicationErrorSink(),
            newId: "new-task");
        using CancellationTokenSource cancellation = new();

        Task<bool> save = editor.SaveAsync(cancellation.Token);
        Assert.True(editor.IsBusy);
        Assert.False(editor.CanEdit);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.False(editor.IsBusy);
        Assert.True(editor.CanEdit);
        Assert.True(editor.CanCancel);
        Assert.True(editor.CanSave);
        Assert.Null(editor.ErrorCode);
    }

    [Fact]
    public async Task UnexpectedSaveDelegateFailure_IsContainedByTheEditor()
    {
        TestApplicationErrorSink errorSink = new();
        TriggerEditorViewModel editor = new(
            Localize,
            original: null,
            existingNames: [],
            (_, _) => Task.FromException<TriggerEditorSaveResult>(
                new InvalidOperationException("save failed")),
            errorSink,
            newId: "new-task");

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("trigger.definition.write_unavailable", editor.ErrorCode);
        Assert.False(editor.IsBusy);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("Triggers.Editor.Save", error.OperationName);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task ProcessFatalSaveFailure_PropagatesAndRestoresEditorAvailability()
    {
        TestApplicationErrorSink errorSink = new();
        TriggerEditorViewModel editor = new(
            Localize,
            original: null,
            existingNames: [],
            (_, _) => Task.FromException<TriggerEditorSaveResult>(
                CreateProcessFatalException<OutOfMemoryException>()),
            errorSink,
            newId: "new-task");

        await Assert.ThrowsAsync<OutOfMemoryException>(
            () => editor.SaveAsync(CancellationToken.None));

        Assert.False(editor.IsBusy);
        Assert.True(editor.CanSave);
        Assert.Empty(errorSink.Errors);
    }

    [Fact]
    public async Task Conflict_RemainsTypedWhenTheBestEffortRefreshIsUnavailable()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")))
        {
            ForceConflict = true,
            ReadExceptionOnCall = 2,
        };
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));
        editor.Name = "Changed";

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("trigger.definition.conflict", editor.ErrorCode);
        Assert.Equal("trigger.definition.conflict", list.ErrorCode);
    }

    [Fact]
    public async Task ConflictRefresh_DoesNotAllowTheStaleEditorToOverwriteTheRemoteDefinition()
    {
        TriggerTaskDefinition original = CompleteDefinition("alpha", "Alpha");
        TriggerTaskDefinition remote = CompleteDefinition("alpha", "Remote");
        RecordingDefinitionStore store = new(Catalog(3, original))
        {
            ForceConflict = true,
            ConflictCatalog = Catalog(4, remote),
        };
        TriggersViewModel list = NewList(store);
        await list.LoadAsync(CancellationToken.None);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(list.BeginEdit("alpha"));
        editor.Name = "Local";

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal("Remote", Assert.Single(list.TriggerTasks).Name);
        Assert.True(editor.IsStale);
        Assert.False(editor.CanEdit);
        Assert.False(editor.CanSave);
        Assert.True(editor.CanCancel);
        store.ForceConflict = false;
        editor.Name = "Local retry";

        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Equal(1, store.ReplaceCallCount);
        Assert.Equal("Remote", Assert.Single(store.Current.Tasks).Definition.Name);
    }

    private static TriggerEditorViewModel NewEditor()
    {
        return new TriggerEditorViewModel(
            Localize,
            original: null,
            existingNames: [],
            (_, _) => Task.FromResult(TriggerEditorSaveResult.Succeeded()),
            new TestApplicationErrorSink(),
            newId: "new-task");
    }

    private static TriggersViewModel NewList(
        RecordingDefinitionStore store,
        TestApplicationErrorSink? errorSink = null)
    {
        return new TriggersViewModel(
            Localize,
            store,
            new FakeTriggerSettings(),
            errorSink ?? new TestApplicationErrorSink());
    }

    private static TException CreateProcessFatalException<TException>()
        where TException : Exception =>
        Activator.CreateInstance<TException>();

    private static TriggerTaskDefinition CompleteDefinition(string id, string name)
    {
        return new TriggerTaskDefinition(
            id,
            4,
            name,
            true,
            [
                new TriggerCondition("entered", TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
                new TriggerCondition("rolling", TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.RollingWindow, 1537, TimeSpan.FromSeconds(90))),
                new TriggerCondition("session", TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(TriggerTrafficScope.CurrentSession, 4097)),
                new TriggerCondition("rate", TriggerConditionKind.Rate,
                    new RateConditionParameters(TriggerTrafficDirection.Download, 8193)),
                new TriggerCondition("connections", TriggerConditionKind.ActiveConnections,
                    new ActiveConnectionsConditionParameters(17)),
                new TriggerCondition("runtime", TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromSeconds(91))),
                new TriggerCondition("time", TriggerConditionKind.SystemTime,
                    new SystemTimeConditionParameters(new TimeOnly(23, 15, 42))),
            ],
            [
                new TriggerAction(TriggerActionKind.SetTransparentProxy, new BooleanActionParameters(true)),
                new TriggerAction(TriggerActionKind.SwitchProxyMode, new ProxyModeActionParameters(ClashSharpMode.RuleTakeover)),
                new TriggerAction(TriggerActionKind.SendNotification, new NotificationActionParameters("  done  ")),
                new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
            ]);
    }

    private static TriggerTaskDefinition FractionalDurationDefinition()
    {
        return new TriggerTaskDefinition(
            "fractional",
            2,
            "Fractional",
            true,
            [
                new TriggerCondition(
                    "rolling",
                    TriggerConditionKind.Traffic,
                    new TrafficConditionParameters(
                        TriggerTrafficScope.RollingWindow,
                        1024,
                        TimeSpan.FromTicks(15_000_000))),
                new TriggerCondition(
                    "runtime",
                    TriggerConditionKind.Runtime,
                    new RuntimeConditionParameters(TimeSpan.FromTicks(905_000_000))),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters("done")),
            ]);
    }

    private static TriggerDefinitionCatalog Catalog(
        long generation,
        params TriggerTaskDefinition[] definitions)
    {
        return new TriggerDefinitionCatalog(
            generation,
            definitions.Select(definition => new TriggerDefinitionCatalogItem(
                definition,
                DateTimeOffset.UnixEpoch.AddDays(generation))),
            []);
    }

    private static string Localize(string key) => key;

    private sealed class FakeTriggerSettings : ITriggerPresentationSettings
    {
        public bool IsEnabled { get; set; } = true;
    }

    private sealed class RecordingDefinitionStore : ITriggerDefinitionStore
    {
        private TriggerDefinitionCatalog _current;

        public RecordingDefinitionStore(TriggerDefinitionCatalog current)
        {
            _current = current;
        }

        public TriggerDefinitionCatalog Current => _current;

        public int ReplaceCallCount { get; private set; }

        public IReadOnlyList<TriggerTaskDefinition>? LastReplacement { get; private set; }

        public Exception? ReplaceException { get; init; }

        public Exception? ReadException { get; init; }

        public TriggerPersistenceResult<TriggerDefinitionCatalog>? ReadResult { get; init; }

        public int? ReadExceptionOnCall { get; init; }

        public bool ForceConflict { get; set; }

        public TriggerDefinitionCatalog? ConflictCatalog { get; init; }

        public TaskCompletionSource<TriggerPersistenceResult<TriggerDefinitionCatalog>>?
            ReplaceCompletion
        { get; init; }

        public int ReadCallCount { get; private set; }

        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            if (ReadException is not null || ReadExceptionOnCall == ReadCallCount)
            {
                throw ReadException ?? new InvalidOperationException("read failed");
            }

            return Task.FromResult(
                ReadResult ?? TriggerPersistenceResult.Succeeded(_current));
        }

        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(
            long expectedGeneration,
            IReadOnlyList<TriggerTaskDefinition> definitions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReplaceException is not null)
            {
                throw ReplaceException;
            }

            ReplaceCallCount++;
            LastReplacement = definitions.ToArray();
            if (ReplaceCompletion is not null)
            {
                return ReplaceCompletion.Task;
            }

            if (ForceConflict || expectedGeneration != _current.Generation)
            {
                _current = ConflictCatalog ?? _current;
                return Task.FromResult(TriggerPersistenceResult.Conflict<TriggerDefinitionCatalog>());
            }

            Dictionary<string, DateTimeOffset?> timestamps = _current.Tasks.ToDictionary(
                static task => task.Definition.Id,
                static task => task.LastTriggeredAt,
                StringComparer.Ordinal);
            _current = new TriggerDefinitionCatalog(
                expectedGeneration + 1,
                definitions.Select(definition => new TriggerDefinitionCatalogItem(
                    definition,
                    timestamps.GetValueOrDefault(definition.Id))),
                _current.Diagnostics);
            return Task.FromResult(TriggerPersistenceResult.Succeeded(_current));
        }
    }
}
