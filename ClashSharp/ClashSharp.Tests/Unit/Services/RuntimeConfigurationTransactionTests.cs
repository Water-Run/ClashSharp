using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for durable mihomo runtime configuration transactions.</summary>
public sealed class RuntimeConfigurationTransactionTests
{
    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_ReadyCandidate_PublishesDesiredAsApplied()
    {
        using TempDirectory tempDirectory = new();
        RecordingValidator validator = new();
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17890,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.Applied, result.Outcome);
        Assert.True(result.IsApplied);
        Assert.False(result.IsDegraded);
        Assert.Equal(1, result.GenerationState.DesiredGeneration);
        Assert.Equal(1, result.GenerationState.AppliedGeneration);
        Assert.Equal(result.GenerationState.DesiredContentHash, result.GenerationState.AppliedContentHash);
        Assert.Equal([1], runtime.AppliedGenerations);
        Assert.Equal([1], runtime.ReadinessGenerations);
        Assert.Equal([result.GenerationState.DesiredContentHash!], runtime.ReadinessConfigurationHashes);
        Assert.Equal([1], runtime.CommittedGenerations);
        Assert.Equal(1, runtime.DeactivateCount);
        Assert.Contains("mixed-port: 17890", File.ReadAllText(result.Configuration.ConfigPath), StringComparison.Ordinal);
        Assert.Single(validator.Paths);
        Assert.Contains(".runtime-staging.", validator.Paths[0], StringComparison.Ordinal);
        AssertNoRuntimeSidecars(tempDirectory.Path);

        RuntimeConfigurationGenerationState persisted =
            await service.GetRuntimeGenerationStateAsync(CancellationToken.None);
        Assert.Equal(result.GenerationState, persisted);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_LegacyFileIsUnverifiedAndRejectedCandidateFailsClosed()
    {
        using TempDirectory tempDirectory = new();
        RecordingValidator validator = new(new InvalidOperationException("rejected"));
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator);
        string baselinePath = service
            .EnsureConfiguration(ClashSharpMode.Standby, transparentProxyEnabled: false, mixedPort: 17891)
            .ConfigPath;
        string baselineText = File.ReadAllText(baselinePath);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17892,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.Rejected, result.Outcome);
        Assert.True(result.IsDegraded);
        Assert.Equal(1, result.GenerationState.DesiredGeneration);
        Assert.Null(result.GenerationState.AppliedGeneration);
        Assert.IsAssignableFrom<InvalidOperationException>(result.Failure);
        Assert.Empty(runtime.AppliedGenerations);
        Assert.Equal(1, runtime.DeactivateCount);
        Assert.False(File.Exists(baselinePath));
        Assert.NotEmpty(baselineText);
        AssertNoRuntimeSidecars(tempDirectory.Path);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_MalformedLegacyFileIsRemovedBeforeCandidateValidation()
    {
        using TempDirectory tempDirectory = new();
        string configurationPath = Path.Combine(tempDirectory.Path, "config.yaml");
        await File.WriteAllTextAsync(configurationPath, "not: [valid");
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(
            tempDirectory.Path,
            new RecordingValidator(new InvalidOperationException("rejected")));

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17892,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.Rejected, result.Outcome);
        Assert.Null(result.GenerationState.AppliedGeneration);
        Assert.Equal(1, runtime.DeactivateCount);
        Assert.Empty(runtime.AppliedGenerations);
        Assert.False(File.Exists(configurationPath));
        AssertNoRuntimeSidecars(tempDirectory.Path);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_ActivationFails_RestoresFileAndRuntimeGeneration()
    {
        using TempDirectory tempDirectory = new();
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult baseline = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17893,
            new RecordingRuntime(),
            CancellationToken.None);
        string baselinePath = baseline.Configuration.ConfigPath;
        string baselineText = File.ReadAllText(baselinePath);
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("candidate failed"));
        runtime.ApplyFailures.Enqueue(null);
        runtime.ReadinessResults.Enqueue(true);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17894,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Outcome);
        Assert.True(result.IsDegraded);
        Assert.Equal([2, 1], runtime.AppliedGenerations);
        Assert.Equal([1], runtime.ReadinessGenerations);
        Assert.Equal(baselineText, File.ReadAllText(baselinePath));
        Assert.Null(result.RollbackFailure);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_ReadinessFails_RestoresPreviousReadyGeneration()
    {
        using TempDirectory tempDirectory = new();
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult baseline = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17895,
            new RecordingRuntime(),
            CancellationToken.None);
        string baselinePath = baseline.Configuration.ConfigPath;
        string baselineText = File.ReadAllText(baselinePath);
        runtime.ReadinessResults.Enqueue(false);
        runtime.ReadinessResults.Enqueue(true);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17896,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Outcome);
        Assert.Equal([2, 1], runtime.AppliedGenerations);
        Assert.Equal([2, 1], runtime.ReadinessGenerations);
        Assert.Equal(
            [result.GenerationState.DesiredContentHash!, baseline.GenerationState.AppliedContentHash!],
            runtime.ReadinessConfigurationHashes);
        Assert.Equal(baselineText, File.ReadAllText(baselinePath));
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_RollbackRuntimeFails_ReportsUnknownRuntimeHealth()
    {
        using TempDirectory tempDirectory = new();
        RecordingRuntime runtime = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult baseline = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17897,
            new RecordingRuntime(),
            CancellationToken.None);
        string baselinePath = baseline.Configuration.ConfigPath;
        string baselineText = File.ReadAllText(baselinePath);
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("candidate failed"));
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("rollback failed"));

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17898,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.RollbackFailed, result.Outcome);
        Assert.True(result.IsDegraded);
        Assert.IsType<InvalidOperationException>(result.RollbackFailure);
        Assert.Equal(baselineText, File.ReadAllText(baselinePath));
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_NetworkCommitFails_RestoresAndCommitsBaseline()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult baseline = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17910,
            new RecordingRuntime(),
            CancellationToken.None);
        string baselineText = File.ReadAllText(baseline.Configuration.ConfigPath);
        RecordingRuntime runtime = new();
        runtime.CommitFailures.Enqueue(new InvalidOperationException("WinINet commit failed"));
        runtime.CommitFailures.Enqueue(null);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17911,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Outcome);
        Assert.Equal([2, 1], runtime.AppliedGenerations);
        Assert.Equal([2, 1], runtime.ReadinessGenerations);
        Assert.Equal([2, 1], runtime.CommittedGenerations);
        Assert.Equal(baselineText, File.ReadAllText(baseline.Configuration.ConfigPath));
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_DisabledGeneration_RemainsRollbackOwnerPlan()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult disabled = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Disabled,
            transparentProxyEnabled: false,
            mixedPort: 17912,
            new RecordingRuntime(),
            CancellationToken.None);
        RuntimeConfigurationIntegrityObservation disabledObservation =
            service.ObserveRuntimeConfigurationIntegrity();
        RecordingRuntime runtime = new();
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("next candidate failed"));
        runtime.ApplyFailures.Enqueue(null);
        runtime.ReadinessResults.Enqueue(true);

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17913,
            runtime,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.Disabled, disabled.GenerationState.AppliedPlan!.Mode);
        Assert.True(disabledObservation.IsKnown);
        Assert.Equal(ClashSharpMode.Disabled, disabledObservation.AppliedPlan!.Mode);
        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Outcome);
        Assert.Equal([ClashSharpMode.Standby, ClashSharpMode.Disabled], runtime.AppliedPlans.Select(plan => plan.Mode));
        Assert.Equal([ClashSharpMode.Disabled], runtime.CommittedPlans.Select(plan => plan.Mode));
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_NoBaselineActivationFailure_DeactivatesAndRemovesCandidate()
    {
        using TempDirectory tempDirectory = new();
        RecordingRuntime runtime = new();
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("candidate failed"));
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());

        RuntimeConfigurationTransactionResult result = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17899,
            runtime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Outcome);
        Assert.Equal(2, runtime.DeactivateCount);
        Assert.False(File.Exists(result.Configuration.ConfigPath));
        Assert.Null(result.GenerationState.AppliedGeneration);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_NewServiceRestoresAppliedSnapshotBeforeRejectedAttempt()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService firstService = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult applied = await firstService.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17900,
            new RecordingRuntime(),
            CancellationToken.None);
        string appliedText = File.ReadAllText(applied.Configuration.ConfigPath);
        File.WriteAllText(applied.Configuration.ConfigPath, "mixed-port: 1\nmode: direct\n");
        Assert.False(firstService.ObserveRuntimeConfigurationIntegrity().IsKnown);
        CoreConfigurationService recoveredService = CreateService(
            tempDirectory.Path,
            new RecordingValidator(new InvalidOperationException("reject next")));
        RecordingRuntime recoveryRuntime = new();

        RuntimeConfigurationTransactionResult rejected = await recoveredService.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17901,
            recoveryRuntime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.Rejected, rejected.Outcome);
        Assert.Equal(2, rejected.GenerationState.DesiredGeneration);
        Assert.Equal(1, rejected.GenerationState.AppliedGeneration);
        Assert.Equal([1], recoveryRuntime.AppliedGenerations);
        Assert.Equal([1], recoveryRuntime.ReadinessGenerations);
        Assert.Equal([1], recoveryRuntime.CommittedGenerations);
        Assert.Equal(appliedText, File.ReadAllText(rejected.Configuration.ConfigPath));
        RuntimeConfigurationGenerationState persisted =
            await recoveredService.GetRuntimeGenerationStateAsync(CancellationToken.None);
        Assert.Equal(1, persisted.DesiredGeneration);
        Assert.Equal(1, persisted.AppliedGeneration);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_DivergentManifestReappliesAndCommitsAppliedGeneration()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService firstService = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult applied = await firstService.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17905,
            new RecordingRuntime(),
            CancellationToken.None);
        string manifestPath = Path.Combine(tempDirectory.Path, "config.runtime-state.json");
        string manifest = File.ReadAllText(manifestPath);
        string divergentManifest = manifest.Replace(
            "\"desiredGeneration\":1",
            "\"desiredGeneration\":2",
            StringComparison.Ordinal);
        Assert.NotEqual(manifest, divergentManifest);
        File.WriteAllText(manifestPath, divergentManifest);
        Assert.False(firstService.ObserveRuntimeConfigurationIntegrity().IsKnown);

        RecordingRuntime recoveryRuntime = new();
        CoreConfigurationService recoveredService = CreateService(
            tempDirectory.Path,
            new RecordingValidator(new InvalidOperationException("reject next")));
        RuntimeConfigurationTransactionResult rejected = await recoveredService.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: false,
            mixedPort: 17906,
            recoveryRuntime,
            CancellationToken.None);

        Assert.Equal(RuntimeConfigurationTransactionOutcome.Rejected, rejected.Outcome);
        Assert.Equal([applied.GenerationState.AppliedGeneration!.Value], recoveryRuntime.AppliedGenerations);
        Assert.Equal([applied.GenerationState.AppliedGeneration!.Value], recoveryRuntime.ReadinessGenerations);
        Assert.Equal([applied.GenerationState.AppliedGeneration!.Value], recoveryRuntime.CommittedGenerations);
        RuntimeConfigurationGenerationState persisted =
            await recoveredService.GetRuntimeGenerationStateAsync(CancellationToken.None);
        Assert.Equal(persisted.AppliedGeneration!.Value, persisted.DesiredGeneration);
        Assert.Equal(persisted.AppliedContentHash, persisted.DesiredContentHash);
        Assert.True(recoveredService.ObserveRuntimeConfigurationIntegrity().IsKnown);
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_MissingImportedProfileFailsClosed()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RecordingRuntime runtime = new();

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ApplyRuntimeConfigurationAsync(
                "missing-profile",
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                mixedPort: 17907,
                runtime,
                CancellationToken.None));

        string missingPath = Assert.IsType<string>(exception.FileName);
        Assert.EndsWith(
            Path.Combine("profiles", "missing-profile", "config.yaml"),
            missingPath,
            StringComparison.Ordinal);
        Assert.Empty(runtime.AppliedGenerations);
        Assert.False(File.Exists(Path.Combine(tempDirectory.Path, "config.yaml")));
    }

    [Fact]
    public async Task ImportAndApplyProfileConfigurationAsync_SourceRollbackFailurePreservesBackup()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        ProfileImportResult imported = await service.ImportProfileConfigurationAsync(
            "profile-one",
            "Profile One",
            ProfileConfiguration("old-node"),
            CancellationToken.None);
        RecordingRuntime runtime = new();
        FileStream? sourceLock = null;
        runtime.Applying = (_, _, _) =>
        {
            sourceLock = new FileStream(
                imported.ConfigPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        };
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("candidate failed"));

        try
        {
            await Assert.ThrowsAsync<AggregateException>(() =>
                service.ImportAndApplyProfileConfigurationAsync(
                    "profile-one",
                    "Profile One",
                    ProfileConfiguration("new-node"),
                    ClashSharpMode.RuleTakeover,
                    effectiveTunEnabled: false,
                    mixedPort: 17908,
                    runtime,
                    CancellationToken.None));

            string profileDirectory = Path.GetDirectoryName(imported.ConfigPath)!;
            string backupPath = Assert.Single(
                Directory.GetFiles(profileDirectory, "config.yaml.runtime-backup.*"));
            Assert.Contains("name: old-node", File.ReadAllText(backupPath), StringComparison.Ordinal);
        }
        finally
        {
            sourceLock?.Dispose();
        }
    }

    [Fact]
    public async Task ImportAndApplyProfileConfigurationAsync_RuntimeFailure_RestoresPreviousProfileSource()
    {
        using TempDirectory tempDirectory = new();
        RecordingValidator validator = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, validator);
        _ = await service.ApplyRuntimeConfigurationAsync(
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            mixedPort: 17902,
            new RecordingRuntime(),
            CancellationToken.None);
        ProfileImportResult imported = await service.ImportProfileConfigurationAsync(
            "profile-one",
            "Profile One",
            ProfileConfiguration("old-node"),
            CancellationToken.None);
        string oldSource = File.ReadAllText(imported.ConfigPath);
        RecordingRuntime runtime = new();
        runtime.ApplyFailures.Enqueue(new InvalidOperationException("candidate failed"));
        runtime.ApplyFailures.Enqueue(null);
        runtime.ReadinessResults.Enqueue(true);

        ProfileRuntimeConfigurationTransactionResult result =
            await service.ImportAndApplyProfileConfigurationAsync(
                "profile-one",
                "Profile One",
                ProfileConfiguration("new-node"),
                ClashSharpMode.RuleTakeover,
                effectiveTunEnabled: false,
                mixedPort: 17903,
                runtime,
                CancellationToken.None);

        Assert.False(result.IsApplied);
        Assert.Equal(RuntimeConfigurationTransactionOutcome.RolledBack, result.Runtime.Outcome);
        Assert.Equal(oldSource, File.ReadAllText(imported.ConfigPath));
        Assert.Equal([2, 1], runtime.AppliedGenerations);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(imported.ConfigPath)!, "config.yaml.runtime-*"));
    }

    [Fact]
    public async Task ImportAndApplyProfileConfigurationAsync_ReadyRuntime_CommitsSourceAndExplicitProfilePlan()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());

        ProfileRuntimeConfigurationTransactionResult result =
            await service.ImportAndApplyProfileConfigurationAsync(
                "profile-one",
                "Profile One",
                ProfileConfiguration("new-node"),
                ClashSharpMode.RuleTakeover,
                effectiveTunEnabled: false,
                mixedPort: 17904,
                new RecordingRuntime(),
                CancellationToken.None);

        Assert.True(result.IsApplied);
        Assert.Equal("profile-one", result.Runtime.GenerationState.AppliedPlan!.ProfileId);
        Assert.Contains("name: new-node", File.ReadAllText(result.Profile.ConfigPath), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(result.Profile.ConfigPath)!, "config.yaml.runtime-*"));
    }

    [Fact]
    public async Task ApplyRuntimeConfigurationAsync_RepeatedSuccess_RetainsOnlyBoundedVerifiedSnapshots()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        RuntimeConfigurationTransactionResult? latest = null;
        for (int index = 0; index < 8; index++)
        {
            latest = await service.ApplyRuntimeConfigurationAsync(
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                mixedPort: 18000 + index,
                new RecordingRuntime(),
                CancellationToken.None);
        }

        Assert.NotNull(latest);
        Assert.Equal(8, latest.GenerationState.AppliedGeneration);
        string snapshotsDirectory = Path.Combine(tempDirectory.Path, "runtime-generations");
        string[] snapshots = Directory.GetFiles(snapshotsDirectory, "*.yaml");
        Assert.Equal(5, snapshots.Length);
        Assert.Contains(snapshots, path => path.Contains("0000000000000000008-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportedProfileReadAndDelete_SerializeThroughManagedProfileBoundary()
    {
        using TempDirectory tempDirectory = new();
        CoreConfigurationService service = CreateService(tempDirectory.Path, new RecordingValidator());
        string source = ProfileConfiguration("node-one");
        _ = await service.ImportProfileConfigurationAsync(
            "profile-one",
            "Profile One",
            source,
            CancellationToken.None);

        string? read = await service.ReadImportedProfileConfigurationAsync(
            "profile-one",
            CancellationToken.None);
        bool deleted = await service.DeleteImportedProfileAsync(
            "profile-one",
            CancellationToken.None);
        bool deletedAgain = await service.DeleteImportedProfileAsync(
            "profile-one",
            CancellationToken.None);

        Assert.Equal(source, read);
        Assert.True(deleted);
        Assert.False(deletedAgain);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteImportedProfileAsync(ProfileCatalogIds.BuiltInDirect, CancellationToken.None));
    }

    private static string ProfileConfiguration(string nodeName)
    {
        return
            "proxies:\n"
            + $"  - name: {nodeName}\n"
            + "    type: direct\n"
            + "proxy-groups:\n"
            + "  - name: GLOBAL\n"
            + "    type: select\n"
            + "    proxies:\n"
            + $"      - {nodeName}\n"
            + "rules:\n"
            + "  - MATCH,DIRECT\n";
    }

    private static CoreConfigurationService CreateService(
        string configurationDirectory,
        ICoreConfigurationValidator validator)
    {
        return new CoreConfigurationService(
            configurationDirectory,
            new FakeSettings(),
            new EmptyMetrics(),
            validator,
            static key => key);
    }

    private static void AssertNoRuntimeSidecars(string configurationDirectory)
    {
        Assert.Empty(Directory.GetFiles(configurationDirectory, "config.yaml.runtime-staging.*"));
        Assert.Empty(Directory.GetFiles(configurationDirectory, "config.yaml.restore.*"));
        Assert.Empty(Directory.GetFiles(configurationDirectory, "config.runtime-state.json.tmp.*"));
    }

    private sealed class FakeSettings : ICoreConfigurationSettings
    {
        public bool TransparentProxyEnabled => false;

        public int MixedPort => 7890;

        public string ActiveProfileId => ProfileCatalogIds.BuiltInDirect;

        public string MihomoControllerSecret { get; } =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    }

    private sealed class EmptyMetrics : ICoreConfigurationProfileMetrics
    {
        public int CountNodes(string configurationText) => 0;

        public int CountRules(string configurationText) => 0;
    }

    private sealed class RecordingValidator(Exception? failure = null) : ICoreConfigurationValidator
    {
        public List<string> Paths { get; } = [];

        public Task ValidateAsync(
            string workingDirectory,
            string configurationPath,
            CancellationToken cancellationToken)
        {
            Paths.Add(configurationPath);
            return failure is null
                ? Task.CompletedTask
                : Task.FromException(failure);
        }
    }

    private sealed class RecordingRuntime : ICoreConfigurationRuntime
    {
        public Queue<Exception?> ApplyFailures { get; } = new();

        public Queue<bool> ReadinessResults { get; } = new();

        public Queue<Exception?> CommitFailures { get; } = new();

        public List<long> AppliedGenerations { get; } = [];

        public List<RuntimeConfigurationActivationPlan> AppliedPlans { get; } = [];

        public List<long> ReadinessGenerations { get; } = [];

        public List<string> ReadinessConfigurationHashes { get; } = [];

        public List<long> CommittedGenerations { get; } = [];

        public List<RuntimeConfigurationActivationPlan> CommittedPlans { get; } = [];

        public int DeactivateCount { get; private set; }

        public Action<CoreConfigurationState, long, RuntimeConfigurationActivationPlan>? Applying { get; set; }

        public Task ApplyAsync(
            CoreConfigurationState configuration,
            long generation,
            RuntimeConfigurationActivationPlan plan,
            CancellationToken cancellationToken)
        {
            AppliedGenerations.Add(generation);
            AppliedPlans.Add(plan);
            Applying?.Invoke(configuration, generation, plan);
            Exception? failure = ApplyFailures.Count == 0 ? null : ApplyFailures.Dequeue();
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task<bool> WaitUntilReadyAsync(
            long generation,
            string configurationHash,
            RuntimeConfigurationActivationPlan plan,
            CancellationToken cancellationToken)
        {
            ReadinessGenerations.Add(generation);
            ReadinessConfigurationHashes.Add(configurationHash);
            bool result = ReadinessResults.Count == 0 || ReadinessResults.Dequeue();
            return Task.FromResult(result);
        }

        public Task DeactivateAsync(CancellationToken cancellationToken)
        {
            DeactivateCount++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(
            long generation,
            RuntimeConfigurationActivationPlan plan,
            CancellationToken cancellationToken)
        {
            CommittedGenerations.Add(generation);
            CommittedPlans.Add(plan);
            Exception? failure = CommitFailures.Count == 0 ? null : CommitFailures.Dequeue();
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "clashsharp-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
