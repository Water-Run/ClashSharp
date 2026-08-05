extern alias ClashSharpUi;
using Microsoft.Data.Sqlite;
using ClashSharpMode = global::ClashSharp.Model.ClashSharpMode;
using CoreConfigurationState = ClashSharpUi::ClashSharp.Model.CoreConfigurationState;
using IProfileCatalogCoreConfiguration = ClashSharpUi::ClashSharp.Service.IProfileCatalogCoreConfiguration;
using IProfileCatalogLog = ClashSharpUi::ClashSharp.Service.IProfileCatalogLog;
using IProfileCatalogRuntime = ClashSharpUi::ClashSharp.Service.IProfileCatalogRuntime;
using IProfileCatalogSettings = ClashSharpUi::ClashSharp.Service.IProfileCatalogSettings;
using LogStorageService = ClashSharpUi::ClashSharp.Service.LogStorageService;
using MihomoCoreOwner = global::ClashSharp.Model.MihomoCoreOwner;
using MihomoServiceStatus = ClashSharpUi::ClashSharp.Model.MihomoServiceStatus;
using ProductionRuntimeSnapshotSource =
    ClashSharpUi::ClashSharp.Presentation.Adapters.MasterControlRuntimeSnapshotSource;
using ProfileCatalogRuntimeImportResult = ClashSharpUi::ClashSharp.Service.ProfileCatalogRuntimeImportResult;
using ProfileCatalogService = ClashSharpUi::ClashSharp.Service.ProfileCatalogService;
using ProfileImportResult = ClashSharpUi::ClashSharp.Model.ProfileImportResult;
using ProfileMutationCoordinator =
    ClashSharpUi::ClashSharp.Service.UncoordinatedProfileCatalogMutationCoordinator;
using RuntimeAdapter = ClashSharpUi::ClashSharp.Presentation.Adapters.MasterControlRuntimeAdapter;
using RuntimeConfigurationActivationPlan =
    ClashSharpUi::ClashSharp.Service.RuntimeConfigurationActivationPlan;
using RuntimeConfigurationIntegrityObservation =
    ClashSharpUi::ClashSharp.Service.RuntimeConfigurationIntegrityObservation;
using RuntimeSnapshot = ClashSharpUi::ClashSharp.ViewModel.MasterControlRuntimeSnapshot;
using RuntimeSnapshotSource =
    ClashSharpUi::ClashSharp.Presentation.Adapters.IMasterControlRuntimeSnapshotSource;
using RuntimeSnapshotWork =
    ClashSharpUi::ClashSharp.Presentation.Adapters.IMasterControlRuntimeSnapshotWork;
using RuntimeTrafficRateSnapshot = ClashSharpUi::ClashSharp.Model.RuntimeTrafficRateSnapshot;
using RuntimeUnavailableException =
    ClashSharpUi::ClashSharp.ViewModel.MasterControlRuntimeUnavailableException;
using StartupRestoreFallbackStatus = ClashSharpUi::ClashSharp.Model.StartupRestoreFallbackStatus;
using TriggerPresentationSummary =
    ClashSharpUi::ClashSharp.Hosting.Compatibility.TriggerPresentationSummary;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class MasterControlRuntimeAdapterTests
{
    [Fact]
    public void GetSnapshotAsync_CapturesOnCallingThreadAndAggregatesOnWorkerThread()
    {
        RecordingRuntimeSnapshotSource source = new();
        RuntimeAdapter adapter = new(source);
        Exception? failure = null;
        int callingThreadId = 0;

        Thread callingThread = new(() =>
        {
            try
            {
                callingThreadId = Environment.CurrentManagedThreadId;
                adapter.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        callingThread.Start();
        Assert.True(callingThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(callingThreadId, source.CaptureThreadId);
        Assert.NotEqual(callingThreadId, source.Work.ExecuteThreadId);
    }

    [Fact]
    public void Capture_DoesNotReadProfileCatalogOrInitializeSqlite()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            string profileCatalogPath = Path.Combine(testDirectory, "ProfileCatalog.json");
            string logDatabasePath = Path.Combine(testDirectory, "logs.sqlite3");
            ProfileCatalogService profileCatalog = new(
                profileCatalogPath,
                Path.Combine(testDirectory, "mihomo", "history"),
                new FixedProfileCatalogSettings(),
                new UnusedProfileCatalogCoreConfiguration(),
                new UnusedProfileCatalogRuntime(),
                new NullProfileCatalogLog(),
                static key => key,
                ProfileMutationCoordinator.Instance);
            LogStorageService logStorage = new(logDatabasePath, static () => "built-in-direct");
            int localizationCallCount = 0;
            ProductionRuntimeSnapshotSource source = new(
                static () => "built-in-direct",
                () => new CoreConfigurationState(testDirectory, string.Empty, false),
                static profileId => profileId,
                profileCatalog,
                logStorage,
                key =>
                {
                    localizationCallCount++;
                    return key;
                },
                static () => MihomoServiceStatus.Unknown(string.Empty),
                static () => new StartupRestoreFallbackStatus(false, string.Empty),
                static () => new RuntimeTrafficRateSnapshot(0, 0, 0, 0, 0),
                static () => new TriggerPresentationSummary(0, 0),
                static () => 0);

            source.Capture();

            Assert.Equal(2, localizationCallCount);
            Assert.False(File.Exists(profileCatalogPath));
            Assert.False(File.Exists(logDatabasePath));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenWorkerThrowsSqliteException_TranslatesStorageFailure()
    {
        SqliteException storageFailure = new("database is unavailable", 14);
        RuntimeAdapter adapter = new(new ThrowingRuntimeSnapshotSource(storageFailure));

        RuntimeUnavailableException exception =
            await Assert.ThrowsAsync<RuntimeUnavailableException>(
                () => adapter.GetSnapshotAsync(CancellationToken.None));

        Assert.Same(storageFailure, exception.InnerException);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenWorkerIsCanceled_PreservesCancellation()
    {
        using CancellationTokenSource cancellationSource = new();
        cancellationSource.Cancel();
        OperationCanceledException cancellation = new(cancellationSource.Token);
        RuntimeAdapter adapter = new(new ThrowingRuntimeSnapshotSource(cancellation));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => adapter.GetSnapshotAsync(CancellationToken.None));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCallerCancels_PassesCallerTokenToWorker()
    {
        using CancellationTokenSource cancellationSource = new();
        CancellationRecordingRuntimeSnapshotSource source = new();
        RuntimeAdapter adapter = new(source);

        Task<RuntimeSnapshot> execution =
            adapter.GetSnapshotAsync(cancellationSource.Token);
        await source.Work.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellationSource.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(cancellationSource.Token, source.Work.ObservedToken);
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public void Capture_DefersFileRegistryAndWorkingSetDelegatesUntilWorkExecutes()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            ProfileCatalogService profileCatalog = new(
                Path.Combine(testDirectory, "ProfileCatalog.json"),
                Path.Combine(testDirectory, "mihomo", "history"),
                new FixedProfileCatalogSettings(),
                new UnusedProfileCatalogCoreConfiguration(),
                new UnusedProfileCatalogRuntime(),
                new NullProfileCatalogLog(),
                static key => key,
                ProfileMutationCoordinator.Instance);
            LogStorageService logStorage = new(
                Path.Combine(testDirectory, "logs.sqlite3"),
                static () => "profile-one");
            int fileCallCount = 0;
            int registryCallCount = 0;
            int workingSetCallCount = 0;
            ProductionRuntimeSnapshotSource source = new(
                static () => "profile-one",
                () =>
                {
                    fileCallCount++;
                    return new CoreConfigurationState(testDirectory, string.Empty, false);
                },
                profileId =>
                {
                    fileCallCount++;
                    return profileId;
                },
                profileCatalog,
                logStorage,
                static key => key,
                static () => MihomoServiceStatus.Unknown(string.Empty),
                () =>
                {
                    registryCallCount++;
                    return new StartupRestoreFallbackStatus(false, string.Empty);
                },
                static () => new RuntimeTrafficRateSnapshot(0, 0, 0, 0, 0),
                static () => new TriggerPresentationSummary(0, 0),
                () =>
                {
                    workingSetCallCount++;
                    return 0;
                });

            RuntimeSnapshotWork work = source.Capture();

            Assert.Equal(0, fileCallCount);
            Assert.Equal(0, registryCallCount);
            Assert.Equal(0, workingSetCallCount);

            work.Execute(CancellationToken.None);

            Assert.Equal(2, fileCallCount);
            Assert.Equal(1, registryCallCount);
            Assert.Equal(1, workingSetCallCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, true, true, true, (int)MihomoCoreOwner.Service, true)]
    [InlineData(true, true, true, false, (int)MihomoCoreOwner.None, true)]
    [InlineData(true, false, true, false, (int)MihomoCoreOwner.None, true)]
    [InlineData(false, true, false, false, (int)MihomoCoreOwner.None, true)]
    [InlineData(true, false, false, false, (int)MihomoCoreOwner.None, false)]
    public void Execute_ClassifiesOnlyOneOwnerThatMatchesSemanticTunPlan(
        bool appCoreRunning,
        bool serviceCoreRunning,
        bool configurationTunEnabled,
        bool expectedKnown,
        int expectedOwnerValue,
        bool integrityKnown)
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            string configurationPath = Path.Combine(testDirectory, "config.yaml");
            File.WriteAllText(
                configurationPath,
                configurationTunEnabled
                    ? "mixed-port: 7890\nmode: rule\ntun:\n  enable: true\n"
                    : "mixed-port: 7890\nmode: rule\n");
            ProfileCatalogService profileCatalog = new(
                Path.Combine(testDirectory, "ProfileCatalog.json"),
                Path.Combine(testDirectory, "mihomo", "history"),
                new FixedProfileCatalogSettings(),
                new UnusedProfileCatalogCoreConfiguration(),
                new UnusedProfileCatalogRuntime(),
                new NullProfileCatalogLog(),
                static key => key,
                ProfileMutationCoordinator.Instance);
            LogStorageService logStorage = new(
                Path.Combine(testDirectory, "logs.sqlite3"),
                static () => "built-in-direct");
            ProductionRuntimeSnapshotSource source = new(
                static () => "built-in-direct",
                () => new CoreConfigurationState(testDirectory, configurationPath, true),
                static _ => null,
                profileCatalog,
                logStorage,
                static key => key,
                () => new MihomoServiceStatus(true, serviceCoreRunning, string.Empty),
                static () => new StartupRestoreFallbackStatus(false, string.Empty),
                static () => new RuntimeTrafficRateSnapshot(0, 0, 0, 0, 0),
                static () => new TriggerPresentationSummary(0, 0),
                static () => 0,
                () => appCoreRunning,
                static () => true,
                () => integrityKnown
                    ? new RuntimeConfigurationIntegrityObservation(
                        true,
                        new RuntimeConfigurationActivationPlan(
                            ClashSharpMode.RuleTakeover,
                            configurationTunEnabled,
                            7890,
                            "built-in-direct"))
                    : RuntimeConfigurationIntegrityObservation.Unknown);

            RuntimeSnapshot snapshot = source.Capture().Execute(CancellationToken.None);

            Assert.Equal(expectedKnown, snapshot.RuntimeOwnershipKnown);
            Assert.Equal((MihomoCoreOwner)expectedOwnerValue, snapshot.EffectiveOwner);
            Assert.Equal(expectedKnown && serviceCoreRunning, snapshot.TunEffective);
            Assert.True(snapshot.TunRequested);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private sealed class RecordingRuntimeSnapshotSource : RuntimeSnapshotSource
    {
        public RecordingRuntimeSnapshotWork Work { get; } = new();

        public int CaptureThreadId { get; private set; }

        public RuntimeSnapshotWork Capture()
        {
            CaptureThreadId = Environment.CurrentManagedThreadId;
            return Work;
        }
    }

    private sealed class RecordingRuntimeSnapshotWork : RuntimeSnapshotWork
    {
        public int ExecuteThreadId { get; private set; }

        public RuntimeSnapshot Execute(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteThreadId = Environment.CurrentManagedThreadId;
            return RuntimeSnapshot.Unavailable;
        }
    }

    private sealed class CancellationRecordingRuntimeSnapshotSource : RuntimeSnapshotSource
    {
        public CancellationRecordingRuntimeSnapshotWork Work { get; } = new();

        public RuntimeSnapshotWork Capture()
        {
            return Work;
        }
    }

    private sealed class CancellationRecordingRuntimeSnapshotWork : RuntimeSnapshotWork
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public RuntimeSnapshot Execute(CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            Started.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation did not stop runtime snapshot work.");
        }
    }

    private sealed class ThrowingRuntimeSnapshotSource(Exception exception) : RuntimeSnapshotSource
    {
        public RuntimeSnapshotWork Capture()
        {
            return new ThrowingRuntimeSnapshotWork(exception);
        }
    }

    private sealed class ThrowingRuntimeSnapshotWork(Exception exception) : RuntimeSnapshotWork
    {
        public RuntimeSnapshot Execute(CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class FixedProfileCatalogSettings : IProfileCatalogSettings
    {
        public string ActiveProfileId { get; set; } = "built-in-direct";
    }

    private sealed class UnusedProfileCatalogCoreConfiguration : IProfileCatalogCoreConfiguration
    {
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public CoreConfigurationState EnsureDefaultConfiguration()
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadImportedProfileConfigurationAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ProfileImportResult> ValidateImportedProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedProfileCatalogRuntime : IProfileCatalogRuntime
    {
        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullProfileCatalogLog : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }
}
