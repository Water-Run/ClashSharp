extern alias ClashSharpUi;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;
using TriggerConditionEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerConditionEditorViewModel;
using TriggerEditorViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerEditorViewModel;
using TriggersViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggersViewModel;
using TriggerTaskItemViewModel = ClashSharpUi::ClashSharp.ViewModel.TriggerTaskItemViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class TriggerEditorViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_RetainsTheEntireUnsavedTriggerDraftAndOptionObjects(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        RecordingDefinitionStore store = new(Catalog(3, CompleteDefinition("alpha", "Alpha")));
        TriggersViewModel page = new(localization.GetString, store, new FakeTriggerSettings(), new TestApplicationErrorSink());
        Assert.True(await page.LoadAsync(CancellationToken.None));
        TriggerTaskItemViewModel task = Assert.Single(page.TriggerTasks);
        TriggerEditorViewModel editor = Assert.IsType<TriggerEditorViewModel>(page.BeginEdit("alpha"));
        editor.Name = "My unsaved draft";
        TriggerConditionEditorViewModel condition = editor.Conditions.First(item => item.Kind == TriggerConditionKind.Traffic);
        editor.SelectedCondition = condition;
        condition.ThresholdText = "987654";
        var selectedOption = editor.SelectedTrafficScopeOption;
        var options = editor.TrafficScopeOptions;
        var conditionIds = editor.Conditions.Select(item => item.Id).ToArray();
        var actions = editor.Actions.ToArray();
        int reads = store.ReadCallCount;
        localization.Language = language;

        page.RefreshLocalization();

        Assert.Same(task, Assert.Single(page.TriggerTasks));
        Assert.Same(editor, page.CurrentEditor);
        Assert.Same(condition, editor.SelectedCondition);
        Assert.Same(options, editor.TrafficScopeOptions);
        Assert.Same(selectedOption, editor.SelectedTrafficScopeOption);
        Assert.Equal("My unsaved draft", editor.Name);
        Assert.Equal("987654", condition.ThresholdText);
        Assert.Equal(conditionIds, editor.Conditions.Select(item => item.Id));
        Assert.Equal(actions.Length, editor.Actions.Count);
        for (int index = 0; index < actions.Length; index++) { Assert.Same(actions[index], editor.Actions[index]); }
        Assert.Equal(localization.GetString("Nav.Triggers"), page.PageTitleText);
        Assert.Equal(localization.GetString("Triggers.Condition.TrafficInWindow"), condition.Title);
        Assert.Equal(reads, store.ReadCallCount);
        Assert.Equal(0, store.ReplaceCallCount);
    }
}
