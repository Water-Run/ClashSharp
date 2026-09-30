extern alias ClashSharpUi;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiService = ClashSharpUi::ClashSharp.Service;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task RestartRecovery_RejectsAnUnrelatedDurablePointerBeforeNativeEffects()
    {
        await using DataGenerationTestDirectory directory = new();
        NetworkSurface network = new();
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using (Fixture previous = new(directory))
        {
            _ = await StartReplacementFixtureAsync(previous, network);
            await using var lease = await previous.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            var baseline = previous.Manager.CurrentManifest;
            Guid operation = Guid.NewGuid();
            await journal.BeginAsync(operation, baseline, await RuntimeOf(previous).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
            await using DataGenerationScope recorded = await previous.CreateEmptyScopeAsync(lease);
            await journal.SetCandidateAsync(operation, recorded.Descriptor, CancellationToken.None);
            await using DataGenerationScope unrelated = await previous.CreateEmptyScopeAsync(lease);
            _ = await directory.Store.PromoteAsync(unrelated.Descriptor, baseline.ContentHash, CancellationToken.None);
        }
        int effects = 0;
        network.BeforeApply = (_, _) => ++effects;
        await using Fixture restarted = new(directory);
        ConfigureRealRuntime(restarted, new StartupPlatform(), new AppearanceSurface(), network);

        StartupStepResult result = await restarted.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(restarted.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, restarted.Admission.State);
        Assert.Equal(0, effects);
        Assert.NotNull(await journal.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedStartupRecovery_PreservesTheCheckpointForTheNextVerifiedAttempt()
    {
        await using DataGenerationTestDirectory directory = new();
        NetworkSurface network = new();
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using (Fixture previous = new(directory))
        {
            _ = await StartReplacementFixtureAsync(previous, network);
            await using var lease = await previous.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            await journal.BeginAsync(Guid.NewGuid(), previous.Manager.CurrentManifest,
                await RuntimeOf(previous).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
        }
        network.Unknown = true;
        await using (Fixture failed = new(directory))
        {
            ConfigureRealRuntime(failed, new StartupPlatform(), new AppearanceSurface(), network);
            var result = await failed.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(failed.Manager),
                new UiService.AppSettingsService(new Dictionary<string, object>())).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
            Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
            Assert.Equal(MutationAdmissionState.RecoveryOnly, failed.Admission.State);
            Assert.NotNull(await journal.ReadPendingAsync(CancellationToken.None));
            Assert.False(RuntimeOf(failed).IsExecutionPublished);
        }
        network.Unknown = false;
        await using Fixture retry = new(directory);
        ConfigureRealRuntime(retry, new StartupPlatform(), new AppearanceSurface(), network);
        var recovered = await retry.CreateStartupStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(retry.Manager),
            new UiService.AppSettingsService(new Dictionary<string, object>())).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        Assert.Equal(StartupStepOutcome.Succeeded, recovered.Outcome);
        Assert.Null(await journal.ReadPendingAsync(CancellationToken.None));
        Assert.True(RuntimeOf(retry).IsExecutionPublished);
    }

    [Theory]
    [InlineData(false, "before-promotion")]
    [InlineData(false, "after-promotion")]
    [InlineData(true, "before-promotion")]
    [InlineData(true, "after-promotion")]
    public async Task CheckpointProcessTermination_LeavesExactlyTheOldOrNewDurableRecord(bool completing, string cut)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using var lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        Guid operation = Guid.NewGuid();
        await journal.BeginAsync(operation, fixture.Manager.CurrentManifest,
            await RuntimeOf(fixture).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        string[] arguments = completing
            ? ["checkpoint-complete", directory.RootPath, operation.ToString("N"), cut]
            : ["checkpoint-candidate", directory.RootPath, operation.ToString("N"), candidate.GenerationId.ToString("N"), candidate.GenerationNumber.ToString(CultureInfo.InvariantCulture), cut];

        await RunCheckpointCrashProbeAsync(arguments);

        UiData.GenerationReplacementCheckpoint? persisted = await new UiData.FileGenerationReplacementJournal(directory.RootPath).ReadPendingAsync(CancellationToken.None);
        if (completing && cut == "after-promotion") { Assert.Null(persisted); }
        else
        {
            Assert.NotNull(persisted);
            Assert.Equal(operation, persisted.OperationId);
            Assert.Equal(!completing && cut == "after-promotion", persisted.Candidate is not null);
            if (persisted.Candidate is not null) { Assert.True(candidate.IsSameGeneration(persisted.Candidate)); }
        }
    }

    [Theory]
    [InlineData("baseline-only")]
    [InlineData("candidate-applied")]
    [InlineData("pointer-promoted")]
    public async Task RestartRecovery_SelectsTheVerifiedDurableDirectoryAndFinishesItsPendingCheckpoint(string phase)
    {
        await using DataGenerationTestDirectory directory = new();
        StartupPlatform startup = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        DataGenerationDescriptor expected;
        Guid operationId = Guid.NewGuid();
        await using (Fixture previous = new(directory))
        {
            previous.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
            ConfigureRealRuntime(previous, startup, appearance, network);
            Assert.Equal(StartupStepOutcome.Succeeded, (await previous.CreateStartupStep(new RuntimeLifetimeRegistry(),
                new UiData.GenerationSamplingRuntime(previous.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
                .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
            var baseline = previous.Manager.CurrentManifest;
            expected = baseline.Descriptor;
            await using MutationAdmissionLease lease = await previous.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            var runtime = RuntimeOf(previous);
            var snapshot = await runtime.ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
            await journal.BeginAsync(operationId, baseline, snapshot, CancellationToken.None);
            _ = runtime.HoldExecutionAdmitted(lease);
            _ = await runtime.TriggerSettings.Scheduler.QuiesceAsync(CancellationToken.None);
            _ = await runtime.Sampling.QuiesceAsync(CancellationToken.None);
            _ = await runtime.Subscriptions.QuiesceAsync(CancellationToken.None);
            await runtime.Network.RestoreConfigurationAsync(new(ClashSharpMode.Disabled, "builtin-direct", false, 23456), CancellationToken.None);
            if (phase != "baseline-only")
            {
                DataGenerationTransition transition = await previous.Manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
                DataGenerationDescriptor candidate = await previous.CreateCandidatePreparer().StageResetAdmittedAsync(transition, lease, CancellationToken.None);
                await journal.SetCandidateAsync(operationId, candidate, CancellationToken.None);
                var prepared = await transition.ExecuteCandidateAsync<UiData.AppDataGenerationRuntime, UiData.GenerationRuntimePreparationResult>(
                    (owned, _, token) => owned.PrepareReplacementAdmittedAsync(lease, token), CancellationToken.None);
                Assert.True(prepared.IsSucceeded, prepared.Code);
                if (phase == "pointer-promoted")
                {
                    _ = await transition.PromoteManifestAsync(directory.Store, CancellationToken.None);
                    transition.SwapToPromoted();
                    expected = candidate;
                }
            }
            // Simulate loss of process-owned services without choosing commit or rollback.
            // The file checkpoint, immutable directories, durable pointer and native surface survive.
            await previous.Manager.DisposeAsync();
        }
        Assert.Equal(operationId, (await journal.ReadPendingAsync(CancellationToken.None))!.OperationId);
        await using Fixture restarted = new(directory);
        ConfigureRealRuntime(restarted, startup, appearance, network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());

        StartupStepResult recovered = await restarted.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(restarted.Manager), settings).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, recovered.Outcome);
        Assert.True(expected.IsSameGeneration(restarted.Manager.CurrentManifest.Descriptor));
        Assert.Equal(phase == "pointer-promoted" ? 10000 : 23456, settings.MixedPort);
        Assert.Equal(settings.MixedPort, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        Assert.Null(await journal.ReadPendingAsync(CancellationToken.None));
        Assert.True(File.Exists(journal.Path));
        Assert.True(RuntimeOf(restarted).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, restarted.Admission.State);
    }

    [Theory]
    [InlineData("before-promotion")]
    [InlineData("after-promotion")]
    public async Task CheckpointWriteFailure_IsClassifiableWithoutStartingNativeEffects(string cut)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        int effects = 0;
        network.BeforeApply = (_, _) => ++effects;
        bool injected = false;
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath, point =>
        {
            if (!injected && point == cut) { injected = true; throw new IOException("checkpoint write interrupted"); }
        });

        await Assert.ThrowsAsync<IOException>(() => fixture.CreateReplacementCoordinator(journal: journal).ResetAllSettingsAsync(CancellationToken.None));

        Assert.True(injected);
        Assert.Equal(0, effects);
        Assert.Equal(before.ContentHash, fixture.Manager.CurrentManifest.ContentHash);
        Assert.Null(await new UiData.FileGenerationReplacementJournal(directory.RootPath).ReadPendingAsync(CancellationToken.None));
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("unknown-field")]
    [InlineData("oversized")]
    public async Task CorruptCheckpoint_BlocksStartupBeforeAnyNativeEffect(string fault)
    {
        await using DataGenerationTestDirectory directory = new();
        NetworkSurface network = new();
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using (Fixture previous = new(directory))
        {
            _ = await StartReplacementFixtureAsync(previous, network);
            await using var lease = await previous.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            await journal.BeginAsync(Guid.NewGuid(), previous.Manager.CurrentManifest,
                await RuntimeOf(previous).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
        }
        if (fault == "oversized") { await File.WriteAllTextAsync(journal.Path, new string('x', 65537)); }
        else
        {
            JsonObject document = JsonNode.Parse(await File.ReadAllTextAsync(journal.Path))!.AsObject();
            if (fault == "hash") { document["hash"] = new string('0', 64); }
            else { document["unexpected"] = true; }
            await File.WriteAllTextAsync(journal.Path, document.ToJsonString());
        }
        byte[] corrupt = await File.ReadAllBytesAsync(journal.Path);
        int effects = 0;
        network.BeforeApply = (_, _) => ++effects;
        await using Fixture restarted = new(directory);
        ConfigureRealRuntime(restarted, new StartupPlatform(), new AppearanceSurface(), network);

        StartupStepResult result = await restarted.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(restarted.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, restarted.Admission.State);
        Assert.Equal(0, effects);
        Assert.Empty(restarted.Containers);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(journal.Path));
    }

    [Fact]
    public async Task PendingCheckpointWithMissingPointer_DoesNotRemigrateOrPublishAnEmptyGeneration()
    {
        await using DataGenerationTestDirectory directory = new();
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using (Fixture previous = new(directory))
        {
            _ = await StartReplacementFixtureAsync(previous, new NetworkSurface());
            await using var lease = await previous.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            await journal.BeginAsync(Guid.NewGuid(), previous.Manager.CurrentManifest,
                await RuntimeOf(previous).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
        }
        File.Delete(directory.Policy.CurrentManifestPath);
        string[] roots = Directory.GetDirectories(directory.Policy.GenerationsRootPath);
        await using Fixture restarted = new(directory);
        ConfigureRealRuntime(restarted, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());

        StartupStepResult result = await restarted.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new UiData.GenerationSamplingRuntime(restarted.Manager), new UiService.AppSettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal(MutationAdmissionState.RecoveryOnly, restarted.Admission.State);
        Assert.Empty(restarted.Containers);
        Assert.Equal(roots, Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.False(File.Exists(directory.Policy.CurrentManifestPath));
        Assert.NotNull(await journal.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoint_RejectsForeignUpdatesAndPreservesTheActiveOperation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using var lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        var baseline = fixture.Manager.CurrentManifest;
        var snapshot = await RuntimeOf(fixture).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None);
        Guid operation = Guid.NewGuid();
        await journal.BeginAsync(operation, baseline, snapshot, CancellationToken.None);
        byte[] original = await File.ReadAllBytesAsync(journal.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync(Guid.NewGuid(), baseline, snapshot, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.SetCandidateAsync(operation, directory.CreateGeneration(3), CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(journal.Path));
        await journal.CompleteAsync(operation, CancellationToken.None);
        await journal.CompleteAsync(operation, CancellationToken.None);
        Assert.Null(await journal.ReadPendingAsync(CancellationToken.None));
        Guid next = Guid.NewGuid();
        await journal.BeginAsync(next, baseline, snapshot, CancellationToken.None);
        Assert.Equal(next, (await journal.ReadPendingAsync(CancellationToken.None))!.OperationId);
    }

    private static async Task RunCheckpointCrashProbeAsync(IReadOnlyList<string> arguments)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ClashSharp", "ClashSharp.slnx"))) { repository = repository.Parent; }
        Assert.NotNull(repository);
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string executable = Path.Combine(repository.FullName, "ClashSharp", "ClashSharp.SettingsProbe", "bin", "x64", configuration,
            "net10.0-windows10.0.22000.0", "ClashSharp.SettingsProbe.dll");
        Assert.True(File.Exists(executable));
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable);
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The checkpoint crash probe did not start.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("The checkpoint crash probe did not terminate."); }
        string output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        string error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        Assert.True(process.ExitCode == 87, $"Probe exit {process.ExitCode}. stdout: {output} stderr: {error}");
    }
}
