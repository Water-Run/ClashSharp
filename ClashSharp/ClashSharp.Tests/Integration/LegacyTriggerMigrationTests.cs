using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies deterministic migration from every supported legacy trigger document shape.</summary>
public sealed class LegacyTriggerMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MigrateAsync_ArrayAndDocumentShapesPreserveTaskIdentityAndOrder(bool arrayShape)
    {
        using MigrationDirectory directory = new();
        string tasks =
            """
            [
              {
                "Id": "first",
                "Name": "First task",
                "IsEnabled": true,
                "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
                "Actions": [{ "Kind": 6, "Value": "first" }],
                "LastTriggeredAt": null
              },
              {
                "Id": "second",
                "Name": "Second task",
                "IsEnabled": false,
                "Conditions": [{ "Kind": 1, "Threshold": 0, "Value": "" }],
                "Actions": [{ "Kind": 0, "Value": "" }],
                "LastTriggeredAt": null
              }
            ]
            """;
        directory.WriteLegacy(arrayShape ? tasks : $$"""{ "Tasks": {{tasks}} }""");

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal(["first", "second"], snapshot.Tasks.Select(record => record.Definition.Id));
        Assert.Equal(["First task", "Second task"], snapshot.Tasks.Select(record => record.Definition.Name));
        Assert.Equal([true, false], snapshot.Tasks.Select(record => record.Definition.IsEnabled));
        Assert.Equal([0, 1], snapshot.Tasks.Select(record => record.Order));
        Assert.Single(Directory.GetFiles(directory.RootPath, "Triggers.json.migration-backup.*"));
        Assert.False(File.Exists(directory.LegacyPath));
    }

    [Fact]
    public async Task MigrateAsync_RoundTripsAllCurrentConditionAndActionKinds()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            {
              "Tasks": [{
                "Id": "complete",
                "Name": "Complete",
                "IsEnabled": true,
                "Conditions": [
                  { "Kind": 0, "Threshold": 0, "Value": "" },
                  { "Kind": 1, "Threshold": 0, "Value": "" },
                  { "Kind": 2, "Threshold": 0, "Value": "CriticalOnly" },
                  { "Kind": 3, "Threshold": 3000, "Value": "" },
                  { "Kind": 4, "Threshold": 4000, "Value": "" },
                  { "Kind": 5, "Threshold": 5000, "Value": "" },
                  { "Kind": 6, "Threshold": 6000, "Value": "" },
                  { "Kind": 7, "Threshold": 7, "Value": "" },
                  { "Kind": 8, "Threshold": 8000, "Value": "" },
                  { "Kind": 9, "Threshold": 90, "Value": "" },
                  { "Kind": 10, "Threshold": 0, "Value": "23:45:12" }
                ],
                "Actions": [
                  { "Kind": 0, "Value": "" },
                  { "Kind": 1, "Value": "true" },
                  { "Kind": 2, "Value": "false" },
                  { "Kind": 3, "Value": "true" },
                  { "Kind": 4, "Value": "RuleTakeover" },
                  { "Kind": 6, "Value": "migrated" },
                  { "Kind": 5, "Value": "" }
                ],
                "LastTriggeredAt": null
              }]
            }
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerTaskDefinition definition = (await directory.ReadSnapshotAsync()).Tasks[0].Definition;

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Equal(11, definition.Conditions.Count);
        Assert.Collection(
            definition.Conditions,
            condition => Assert.Equal(TriggerEventKind.AppEntered, Assert.IsType<EventConditionParameters>(condition.Parameters).EventKind),
            condition => Assert.Equal(TriggerEventKind.ProxyStarted, Assert.IsType<EventConditionParameters>(condition.Parameters).EventKind),
            condition => Assert.Equal(TriggerNotificationLevel.CriticalOnly, Assert.IsType<NotificationConditionParameters>(condition.Parameters).MinimumLevel),
            condition => Assert.Equal(TriggerTrafficScope.AllTime, Assert.IsType<TrafficConditionParameters>(condition.Parameters).Scope),
            condition => Assert.Equal(TriggerTrafficScope.RollingWindow, Assert.IsType<TrafficConditionParameters>(condition.Parameters).Scope),
            condition => Assert.Equal(TriggerTrafficDirection.Upload, Assert.IsType<RateConditionParameters>(condition.Parameters).Direction),
            condition => Assert.Equal(TriggerTrafficDirection.Download, Assert.IsType<RateConditionParameters>(condition.Parameters).Direction),
            condition => Assert.Equal(7, Assert.IsType<ActiveConnectionsConditionParameters>(condition.Parameters).Threshold),
            condition => Assert.Equal(TriggerTrafficScope.CurrentSession, Assert.IsType<TrafficConditionParameters>(condition.Parameters).Scope),
            condition => Assert.Equal(TimeSpan.FromSeconds(90), Assert.IsType<RuntimeConditionParameters>(condition.Parameters).Threshold),
            condition => Assert.Equal(new TimeOnly(23, 45, 12), Assert.IsType<SystemTimeConditionParameters>(condition.Parameters).TargetTime));
        Assert.Equal(
            [
                TriggerActionKind.CloseConnections,
                TriggerActionKind.SetLaunchAtStartup,
                TriggerActionKind.SetTransparentProxy,
                TriggerActionKind.SetConnectionSampling,
                TriggerActionKind.SwitchProxyMode,
                TriggerActionKind.SendNotification,
                TriggerActionKind.ExitApplication,
            ],
            definition.Actions.Select(action => action.Kind));
        Assert.All(definition.Conditions, condition => Assert.StartsWith("legacy-condition-", condition.Id));
    }

    [Fact]
    public async Task MigrateAsync_RoundTripsTheInitialLegacyEnumLayout()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "initial", "Name": "Initial schema", "IsEnabled": true,
              "Conditions": [
                { "Kind": 0, "Threshold": 0, "Value": "" },
                { "Kind": 1, "Threshold": 0, "Value": "" },
                { "Kind": 2, "Threshold": 0, "Value": "Default" },
                { "Kind": 3, "Threshold": 3000, "Value": "" },
                { "Kind": 4, "Threshold": 4000, "Value": "" },
                { "Kind": 5, "Threshold": 90, "Value": "" },
                { "Kind": 6, "Threshold": 0, "Value": "08:15" }
              ],
              "Actions": [
                { "Kind": 0, "Value": "" },
                { "Kind": 1, "Value": "true" },
                { "Kind": 2, "Value": "RuleTakeover" },
                { "Kind": 4, "Value": "initial" },
                { "Kind": 3, "Value": "" }
              ]
            }]
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerTaskDefinition definition = (await directory.ReadSnapshotAsync()).Tasks[0].Definition;

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Collection(
            definition.Conditions,
            condition => Assert.Equal(TriggerEventKind.AppEntered, Assert.IsType<EventConditionParameters>(condition.Parameters).EventKind),
            condition => Assert.Equal(TriggerEventKind.ProxyStarted, Assert.IsType<EventConditionParameters>(condition.Parameters).EventKind),
            condition => Assert.Equal(TriggerNotificationLevel.Default, Assert.IsType<NotificationConditionParameters>(condition.Parameters).MinimumLevel),
            condition => Assert.Equal(TriggerTrafficScope.AllTime, Assert.IsType<TrafficConditionParameters>(condition.Parameters).Scope),
            condition => Assert.Equal(TriggerTrafficScope.RollingWindow, Assert.IsType<TrafficConditionParameters>(condition.Parameters).Scope),
            condition => Assert.Equal(TimeSpan.FromSeconds(90), Assert.IsType<RuntimeConditionParameters>(condition.Parameters).Threshold),
            condition => Assert.Equal(new TimeOnly(8, 15), Assert.IsType<SystemTimeConditionParameters>(condition.Parameters).TargetTime));
        Assert.Equal(
            [
                TriggerActionKind.CloseConnections,
                TriggerActionKind.SetTransparentProxy,
                TriggerActionKind.SwitchProxyMode,
                TriggerActionKind.SendNotification,
                TriggerActionKind.ExitApplication,
            ],
            definition.Actions.Select(action => action.Kind));
    }

    [Fact]
    public async Task MigrateAsync_AmbiguousNumericLayoutQuarantinesInsteadOfGuessing()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "ambiguous", "Name": "Ambiguous", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 1, "Value": "true" }]
            }]
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Empty(snapshot.Tasks);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == "trigger.migration.task.quarantined"
            && diagnostic.Detail.Contains("action_schema_ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MigrateAsync_MixedDocumentSalvagesIndividuallyIdentifiableLayouts()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "initial", "Name": "Initial", "IsEnabled": true,
              "Conditions": [{ "Kind": 6, "Threshold": 0, "Value": "08:15" }],
              "Actions": [{ "Kind": 4, "Value": "initial" }]
            }, {
              "Id": "current", "Name": "Current", "IsEnabled": true,
              "Conditions": [{ "Kind": 10, "Threshold": 0, "Value": "09:30" }],
              "Actions": [{ "Kind": 6, "Value": "current" }]
            }, {
              "Id": "ambiguous", "Name": "Ambiguous", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 1, "Value": "true" }]
            }]
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Equal(["initial", "current"], snapshot.Tasks.Select(task => task.Definition.Id));
        Assert.All(snapshot.Tasks, task =>
            Assert.Equal(TriggerActionKind.SendNotification, Assert.Single(task.Definition.Actions).Kind));
        Assert.Single(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == "trigger.migration.task.quarantined"
            && diagnostic.TaskId == "ambiguous");
    }

    [Fact]
    public async Task MigrateAsync_LastTriggeredAtMapsToHistoryAndTypedLatchesWithoutOutbox()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "latched",
              "Name": "Latched",
              "IsEnabled": true,
              "Conditions": [
                { "Kind": 0, "Threshold": 0, "Value": "" },
                { "Kind": 3, "Threshold": 3000, "Value": "" },
                { "Kind": 4, "Threshold": 4000, "Value": "" },
                { "Kind": 5, "Threshold": 5000, "Value": "" },
                { "Kind": 7, "Threshold": 7, "Value": "" },
                { "Kind": 8, "Threshold": 8000, "Value": "" },
                { "Kind": 9, "Threshold": 90, "Value": "" },
                { "Kind": 10, "Threshold": 0, "Value": "08:30" }
              ],
              "Actions": [{ "Kind": 6, "Value": "migrated" }],
              "LastTriggeredAt": "2026-07-22T23:30:00+02:00"
            }]
            """);

        await directory.CreateCoordinator().MigrateAsync(CancellationToken.None);
        TriggerTaskRecord record = (await directory.ReadSnapshotAsync()).Tasks[0];
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> outbox =
            await directory.Repository.ReadRecoverableActionsAsync(CancellationToken.None);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 22, 23, 30, 0, TimeSpan.FromHours(2)),
            record.State.LastTriggeredAt);
        Assert.Null(record.State.ConditionStates["legacy-condition-0000"].ConsumedDate);
        Assert.Equal(1, record.State.ConditionStates["legacy-condition-0001"].ConsumedRevision);
        Assert.False(record.State.ConditionStates["legacy-condition-0002"].IsArmed);
        Assert.False(record.State.ConditionStates["legacy-condition-0003"].IsArmed);
        Assert.False(record.State.ConditionStates["legacy-condition-0004"].IsArmed);
        Assert.False(record.State.ConditionStates["legacy-condition-0005"].IsArmed);
        Assert.False(record.State.ConditionStates["legacy-condition-0006"].IsArmed);
        Assert.Equal(new DateOnly(2026, 7, 22), record.State.ConditionStates["legacy-condition-0007"].ConsumedDate);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(outbox.Value));
    }

    [Fact]
    public async Task MigrateAsync_DuplicateTaskIdentityAndNameNormalizeWithStableDiagnostics()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "duplicate", "Name": "Same", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "first" }]
            }, {
              "Id": "duplicate", "Name": "Same", "IsEnabled": true,
              "Conditions": [{ "Kind": 1, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "second" }]
            }]
            """);

        await directory.CreateCoordinator().MigrateAsync(CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(2, snapshot.Tasks.Count);
        Assert.Equal("duplicate", snapshot.Tasks[0].Definition.Id);
        Assert.NotEqual("duplicate", snapshot.Tasks[1].Definition.Id);
        Assert.Equal("Same", snapshot.Tasks[0].Definition.Name);
        Assert.Equal("Same (2)", snapshot.Tasks[1].Definition.Name);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.migration.task_id.normalized");
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.migration.task_name.normalized");
    }

    [Fact]
    public async Task MigrateAsync_InvalidTasksAreQuarantinedWhileValidTasksCommitAtomically()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            { "Tasks": [{
              "Id": "valid", "Name": "Valid", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "valid" }]
            }, {
              "Id": "bad-enum", "Name": "Bad enum", "IsEnabled": true,
              "Conditions": [{ "Kind": 999, "Threshold": 1, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "bad" }]
            }, {
              "Id": "bad-parameter", "Name": "Bad parameter", "IsEnabled": true,
              "Conditions": [{ "Kind": 4, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 1, "Value": "not-a-bool" }]
            }] }
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Migrated, result.Status);
        Assert.Equal("valid", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.migration.task.quarantined");
        string quarantinePath = Assert.Single(Directory.GetFiles(
            directory.RootPath,
            "Triggers.json.task-quarantine.*"));
        string quarantine = await File.ReadAllTextAsync(quarantinePath, CancellationToken.None);
        Assert.Contains("bad-enum", quarantine, StringComparison.Ordinal);
        Assert.Contains("bad-parameter", quarantine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Id\": \"valid\"", quarantine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"Tasks\": [")]
    [InlineData("{ \"NotTasks\": [] }")]
    [InlineData("null")]
    public async Task MigrateAsync_MalformedDocumentQuarantinesWholeSourceAndPersistsDiagnostic(
        string json)
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(json);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Quarantined, result.Status);
        Assert.Empty(snapshot.Tasks);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Code == "trigger.migration.document.quarantined");
        Assert.Single(Directory.GetFiles(directory.RootPath, "Triggers.json.quarantine.*"));
        Assert.False(File.Exists(directory.LegacyPath));
    }

    [Fact]
    public async Task MigrateAsync_LockedLegacySourceReturnsUnavailableWithoutCreatingDatabase()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy("[]");
        using FileStream lease = new(
            directory.LegacyPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);

        Assert.Equal(TriggerMigrationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(directory.DatabasePath));
        Assert.True(File.Exists(directory.LegacyPath));
    }

    [Fact]
    public async Task MigrateAsync_ValidExistingDatabaseTakesPrecedenceWithoutReadingLegacy()
    {
        using MigrationDirectory directory = new();
        await directory.OpenRepositoryAsync();
        await directory.Repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [CurrentDefinition("database")]),
            CancellationToken.None);
        directory.WriteLegacy("{ malformed");

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.ExistingDatabasePreferred, result.Status);
        Assert.Equal("database", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.True(File.Exists(directory.LegacyPath));
        Assert.Empty(Directory.GetFiles(directory.RootPath, "Triggers.json.quarantine.*"));
    }

    [Fact]
    public async Task MigrateAsync_ValidExistingEmptyDatabaseTakesPrecedenceWithoutAnIntent()
    {
        using MigrationDirectory directory = new();
        await directory.OpenRepositoryAsync();
        directory.WriteLegacy(
            """
            [{
              "Id": "legacy", "Name": "Legacy", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "legacy" }]
            }]
            """);

        TriggerMigrationResult result = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.ExistingDatabasePreferred, result.Status);
        Assert.Equal(0, snapshot.DefinitionGeneration);
        Assert.Empty(snapshot.Tasks);
        Assert.True(File.Exists(directory.LegacyPath));
        Assert.False(File.Exists(directory.LegacyPath + ".migration-intent"));
    }

    [Fact]
    public async Task MigrateAsync_RepeatedLaunchFinalizesBackupWithoutRepeatingImport()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "once", "Name": "Once", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "once" }]
            }]
            """);

        TriggerMigrationResult first = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        string backup = Assert.Single(Directory.GetFiles(
            directory.RootPath,
            "Triggers.json.migration-backup.*"));
        TriggerMigrationResult second = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerMigrationResult third = await directory.CreateCoordinator().MigrateAsync(
            CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync();

        Assert.Equal(TriggerMigrationStatus.Migrated, first.Status);
        Assert.Equal(TriggerMigrationStatus.Finalized, second.Status);
        Assert.Equal(TriggerMigrationStatus.ExistingDatabasePreferred, third.Status);
        Assert.False(File.Exists(backup));
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal("once", Assert.Single(snapshot.Tasks).Definition.Id);
    }

    [Fact]
    public async Task MigrateAsync_BeforeCommitFailureLeavesAResumableMigrationAuthority()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "resumable", "Name": "Resumable", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "resume" }]
            }]
            """);
        SqliteTriggerRepository interruptedRepository = directory.CreateRepository(
            new ThrowingFaultInjector(TriggerPersistenceFaultPoint.BeforeMigrationCommit));

        await Assert.ThrowsAsync<InjectedMigrationFailure>(() =>
            directory.CreateCoordinator(interruptedRepository).MigrateAsync(CancellationToken.None));

        SqliteTriggerRepository recoveredRepository = directory.CreateRepository();
        TriggerMigrationResult recovered = await directory.CreateCoordinator(recoveredRepository)
            .MigrateAsync(CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync(recoveredRepository);

        Assert.Equal(TriggerMigrationStatus.Migrated, recovered.Status);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal("resumable", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.False(File.Exists(directory.LegacyPath));
    }

    [Fact]
    public async Task MigrateAsync_AfterCommitFailureFinalizesTheCommittedMigrationOnRestart()
    {
        using MigrationDirectory directory = new();
        directory.WriteLegacy(
            """
            [{
              "Id": "committed", "Name": "Committed", "IsEnabled": true,
              "Conditions": [{ "Kind": 0, "Threshold": 0, "Value": "" }],
              "Actions": [{ "Kind": 6, "Value": "committed" }]
            }]
            """);
        SqliteTriggerRepository interruptedRepository = directory.CreateRepository(
            new ThrowingFaultInjector(TriggerPersistenceFaultPoint.AfterMigrationCommit));

        await Assert.ThrowsAsync<InjectedMigrationFailure>(() =>
            directory.CreateCoordinator(interruptedRepository).MigrateAsync(CancellationToken.None));

        SqliteTriggerRepository recoveredRepository = directory.CreateRepository();
        TriggerMigrationResult recovered = await directory.CreateCoordinator(recoveredRepository)
            .MigrateAsync(CancellationToken.None);
        TriggerRepositorySnapshot snapshot = await directory.ReadSnapshotAsync(recoveredRepository);

        Assert.Equal(TriggerMigrationStatus.Finalized, recovered.Status);
        Assert.Equal(1, snapshot.DefinitionGeneration);
        Assert.Equal("committed", Assert.Single(snapshot.Tasks).Definition.Id);
        Assert.False(File.Exists(directory.LegacyPath));
        Assert.Single(Directory.GetFiles(directory.RootPath, "Triggers.json.migration-backup.*"));
        Assert.Empty(Directory.GetFiles(directory.RootPath, "Triggers.json.migration-intent"));
    }

    private static TriggerTaskDefinition CurrentDefinition(string id)
    {
        return new TriggerTaskDefinition(
            id,
            1,
            id,
            true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [
                new TriggerAction(
                    TriggerActionKind.SendNotification,
                    new NotificationActionParameters(id)),
            ]);
    }

    private sealed class MigrationDirectory : IDisposable
    {
        public MigrationDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "ClashSharp",
                "TriggerMigrationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Repository = new SqliteTriggerRepository(DatabasePath, busyTimeout: TimeSpan.FromSeconds(1));
        }

        public string RootPath { get; }

        public string LegacyPath => Path.Combine(RootPath, "Triggers.json");

        public string DatabasePath => Path.Combine(RootPath, "Triggers.db");

        public SqliteTriggerRepository Repository { get; }

        public TriggerMigrationCoordinator CreateCoordinator()
        {
            return new TriggerMigrationCoordinator(Repository, LegacyPath);
        }

        public TriggerMigrationCoordinator CreateCoordinator(SqliteTriggerRepository repository)
        {
            return new TriggerMigrationCoordinator(repository, LegacyPath);
        }

        public SqliteTriggerRepository CreateRepository(
            ITriggerPersistenceFaultInjector? faultInjector = null)
        {
            return new SqliteTriggerRepository(
                DatabasePath,
                faultInjector,
                TimeSpan.FromSeconds(1));
        }

        public void WriteLegacy(string json)
        {
            File.WriteAllText(LegacyPath, json);
        }

        public async Task OpenRepositoryAsync()
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> result =
                await Repository.OpenAsync(CancellationToken.None);
            Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        }

        public async Task<TriggerRepositorySnapshot> ReadSnapshotAsync()
        {
            return await ReadSnapshotAsync(Repository);
        }

        public async Task<TriggerRepositorySnapshot> ReadSnapshotAsync(
            SqliteTriggerRepository repository)
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> result =
                await repository.ReadSnapshotAsync(CancellationToken.None);
            Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
            return Assert.IsType<TriggerRepositorySnapshot>(result.Value);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class ThrowingFaultInjector(TriggerPersistenceFaultPoint faultPoint)
        : ITriggerPersistenceFaultInjector
    {
        public Task InjectAsync(
            TriggerPersistenceFaultPoint observedFaultPoint,
            CancellationToken cancellationToken)
        {
            if (observedFaultPoint == faultPoint)
            {
                throw new InjectedMigrationFailure();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedMigrationFailure : Exception;
}
