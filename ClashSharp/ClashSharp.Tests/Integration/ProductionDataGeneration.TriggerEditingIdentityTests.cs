extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;
using UiComposition = ClashSharpUi::ClashSharp.Presentation.Composition;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiView = ClashSharpUi::ClashSharp.ViewModel;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task TriggerDataIdentity_SameNumericRevisionInAnotherDirectoryCannotAcceptAnOldWrite()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        ITriggerDefinitionStore store = new UiData.GenerationTriggerDefinitionStore(fixture.Manager);
        var initial = (await store.ReadAsync(CancellationToken.None)).Value!;
        var written = (await store.ReplaceAsync(initial.Version, [IdentityTrigger("shared", "Original")], CancellationToken.None)).Value!;
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        var current = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.Equal(written.Generation, current.Generation);
        Assert.NotEqual(written.Version.DataGenerationId, current.Version.DataGenerationId);

        var stale = await store.ReplaceAsync(written.Version, [IdentityTrigger("shared", "Stale overwrite")], CancellationToken.None);
        var unscoped = await store.ReplaceAsync(new(null, current.Generation), [], CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Conflict, stale.Status);
        Assert.Equal(TriggerPersistenceStatus.Conflict, unscoped.Status);
        var after = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.Equal(current.Version, after.Version);
        Assert.Equal("Original", Assert.Single(after.Tasks).Definition.Name);
    }

    [Fact]
    public async Task TriggerDataIdentity_EditorSurvivesRefreshButCannotSaveIntoTheNewDirectory()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        ITriggerDefinitionStore store = new UiData.GenerationTriggerDefinitionStore(fixture.Manager);
        var initial = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.True((await store.ReplaceAsync(initial.Version, [IdentityTrigger("shared", "Original")], CancellationToken.None)).IsSucceeded);
        UiView.TriggersViewModel page = new(key => key, store, new IdentityTriggerSettings(), new IdentityTriggerErrors());
        Assert.True(await page.LoadAsync(CancellationToken.None));
        var oldRow = Assert.Single(page.TriggerTasks);
        var editor = page.BeginEdit(oldRow.Id)!;
        editor.Name = "Unsaved draft";
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        Assert.True(await page.RefreshAfterDataChangeAsync(CancellationToken.None));

        Assert.Same(editor, page.CurrentEditor);
        Assert.Equal("Unsaved draft", editor.Name);
        Assert.False(page.IsCurrentTask(oldRow));
        Assert.NotSame(oldRow, Assert.Single(page.TriggerTasks));
        Assert.False(await editor.SaveAsync(CancellationToken.None));
        Assert.Same(editor, page.CurrentEditor);
        Assert.Equal("Original", Assert.Single((await store.ReadAsync(CancellationToken.None)).Value!.Tasks).Definition.Name);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("toggle")]
    [InlineData("move")]
    [InlineData("toggle-all")]
    public async Task TriggerDataIdentity_StaleListMutationsConflictAndRefreshInsteadOfChangingTheNewCatalog(string command)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        ITriggerDefinitionStore store = new UiData.GenerationTriggerDefinitionStore(fixture.Manager);
        var initial = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.True((await store.ReplaceAsync(initial.Version,
            [IdentityTrigger("one", "One"), IdentityTrigger("two", "Two")], CancellationToken.None)).IsSucceeded);
        UiView.TriggersViewModel page = new(key => key, store, new IdentityTriggerSettings(), new IdentityTriggerErrors());
        Assert.True(await page.LoadAsync(CancellationToken.None));
        var previous = page.CatalogVersion;
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        bool succeeded = command switch
        {
            "delete" => await page.DeleteTaskAsync("one", CancellationToken.None),
            "toggle" => await page.SetTaskEnabledAsync("one", false, CancellationToken.None),
            "move" => await page.MoveTaskAsync("one", 1, CancellationToken.None),
            _ => await page.SetAllTasksEnabledAsync(false, CancellationToken.None),
        };

        Assert.False(succeeded);
        Assert.NotEqual(previous.DataGenerationId, page.CatalogVersion.DataGenerationId);
        var current = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.Equal(["one", "two"], current.Tasks.Select(task => task.Definition.Id));
        Assert.All(current.Tasks, task => Assert.True(task.Definition.IsEnabled));
    }

    [Fact]
    public async Task TriggerDataIdentity_DeleteConfirmationCannotAdoptARefreshedCatalogVersion()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        ITriggerDefinitionStore store = new UiData.GenerationTriggerDefinitionStore(fixture.Manager);
        var initial = (await store.ReadAsync(CancellationToken.None)).Value!;
        Assert.True((await store.ReplaceAsync(initial.Version, [IdentityTrigger("shared", "Original")], CancellationToken.None)).IsSucceeded);
        UiView.TriggersViewModel page = new(key => key, store, new IdentityTriggerSettings(), new IdentityTriggerErrors());
        Assert.True(await page.LoadAsync(CancellationToken.None));
        var confirmedVersion = page.CatalogVersion;
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        Assert.True(await page.RefreshAfterDataChangeAsync(CancellationToken.None));

        Assert.False(await page.DeleteTaskAsync("shared", confirmedVersion, CancellationToken.None));

        Assert.Single((await store.ReadAsync(CancellationToken.None)).Value!.Tasks);
    }

    [Fact]
    public async Task TriggerDataIdentity_GenerationNotificationsDoNotRequirePreferenceChangesAndAreDisposable()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        int notifications = 0;
        using var subscription = new UiComposition.DataGenerationSubscription(settings, () => notifications++);
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        Assert.Equal(1, notifications);
        subscription.Dispose();
        _ = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task TriggerDataIdentity_FailedPreferenceObserverCannotSuppressTheDirectoryRefresh()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues["MixedPort"] = 23456;
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        settings.SettingChanged += (_, _) => throw new IOException("preference observer unavailable");
        int notifications = 0;
        using var subscription = new UiComposition.DataGenerationSubscription(settings, () => notifications++);

        var result = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        Assert.Equal(1, notifications);
        Assert.True(result.RequiresRestart);
        Assert.Contains("data.replacement.notification_failed", result.Warnings);
    }

    [Fact]
    public async Task TriggerDataIdentity_FailedDirectoryObserverDoesNotPreventLaterPageRefreshes()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        using var broken = new UiComposition.DataGenerationSubscription(settings, () => throw new IOException("unavailable page"));
        int delivered = 0;
        using var working = new UiComposition.DataGenerationSubscription(settings, () => delivered++);

        var result = await fixture.CreateReplacementCoordinator().ResetAllSettingsAsync(CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.True(result.RequiresRestart);
    }

    private static TriggerTaskDefinition IdentityTrigger(string id, string name) => new(id, 1, name, true,
        [new TriggerCondition("entered", TriggerConditionKind.Event, new EventConditionParameters(TriggerEventKind.AppEntered))],
        [new TriggerAction(TriggerActionKind.SendNotification, new NotificationActionParameters("fixture"))]);

    private sealed class IdentityTriggerSettings : UiView.ITriggerPresentationSettings { public bool IsEnabled => true; }
    private sealed class IdentityTriggerErrors : IApplicationErrorSink
    {
        public Task ReportAsync(ApplicationError error, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected trigger error.", error.Exception);
    }
}
