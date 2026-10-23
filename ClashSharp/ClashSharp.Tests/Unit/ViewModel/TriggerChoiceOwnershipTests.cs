extern alias ClashSharpUi;
using TriggerActionKind = ClashSharp.Model.Triggers.TriggerActionKind;
using TriggerConditionTemplate = ClashSharpUi::ClashSharp.ViewModel.TriggerConditionTemplate;
using TriggerEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerEditorViewModel;
using TriggersViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggersViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class TriggerEditorViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChoiceFromAnOldPicker_DoesNotEditACancelledOrReplacedDraft(bool replace)
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel page = new(Localize, store, new FakeTriggerSettings(), new TestApplicationErrorSink());
        TriggerEditorViewModel old = page.BeginCreate();
        int oldConditions = old.Conditions.Count;
        int oldActions = old.Actions.Count;
        if (replace) { page.BeginCreate(); }
        else { page.CancelEdit(); }
        TriggerEditorViewModel? current = page.CurrentEditor;
        int? currentConditions = current?.Conditions.Count;
        int? currentActions = current?.Actions.Count;

        Assert.False(page.TryAddCondition(old, TriggerConditionTemplate.ProxyStarted, CancellationToken.None));
        Assert.False(page.TryAddAction(old, TriggerActionKind.CloseConnections, CancellationToken.None));

        Assert.Equal(oldConditions, old.Conditions.Count);
        Assert.Equal(oldActions, old.Actions.Count);
        Assert.Equal(currentConditions, current?.Conditions.Count);
        Assert.Equal(currentActions, current?.Actions.Count);
        Assert.Equal(0, store.ReplaceCallCount);
    }

    [Fact]
    public void CancelledPage_DoesNotApplyACompletedChoice()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel page = new(Localize, store, new FakeTriggerSettings(), new TestApplicationErrorSink());
        TriggerEditorViewModel editor = page.BeginCreate();
        int conditions = editor.Conditions.Count;
        int actions = editor.Actions.Count;
        using CancellationTokenSource pageLifetime = new();
        pageLifetime.Cancel();

        Assert.Throws<OperationCanceledException>(() => page.TryAddCondition(editor, TriggerConditionTemplate.ProxyStarted, pageLifetime.Token));
        Assert.Throws<OperationCanceledException>(() => page.TryAddAction(editor, TriggerActionKind.CloseConnections, pageLifetime.Token));

        Assert.Equal(conditions, editor.Conditions.Count);
        Assert.Equal(actions, editor.Actions.Count);
        Assert.Equal(0, store.ReplaceCallCount);
    }

    [Fact]
    public void CurrentChoices_OnlyUpdateTheCurrentUnsavedDraft()
    {
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel page = new(Localize, store, new FakeTriggerSettings(), new TestApplicationErrorSink());
        TriggerEditorViewModel editor = page.BeginCreate();
        int conditions = editor.Conditions.Count;
        int actions = editor.Actions.Count;

        Assert.True(page.TryAddCondition(editor, TriggerConditionTemplate.ProxyStarted, CancellationToken.None));
        Assert.True(page.TryAddAction(editor, TriggerActionKind.CloseConnections, CancellationToken.None));

        Assert.Equal(conditions + 1, editor.Conditions.Count);
        Assert.Equal(actions + 1, editor.Actions.Count);
        Assert.Same(editor, page.CurrentEditor);
        Assert.Equal(0, store.ReplaceCallCount);
    }
}
