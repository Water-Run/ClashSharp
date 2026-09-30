using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Exercises real staging and child ownership with isolated process and readiness boundaries.</summary>
public sealed class MihomoDataGenerationTests
{
    [Fact]
    public async Task Staging_SeparatesVersionNumbersAcrossDataDirectoriesAndRejectsConflictsWithinOne()
    {
        await using MihomoChildSupervisorTestContext context = new([], generationLayout: true);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        string firstHash = WriteGeneration(context, first, "mixed-port: 7101\n");
        string secondHash = WriteGeneration(context, second, "mixed-port: 7102\n");
        MihomoGenerationStore store = new(context.Options, protectDirectory: false);

        MihomoStagedGeneration a = await store.StageAsync(1, firstHash, CancellationToken.None, dataGenerationId: first);
        MihomoStagedGeneration b = await store.StageAsync(1, secondHash, CancellationToken.None, dataGenerationId: second);

        Assert.NotEqual(a.ConfigurationPath, b.ConfigurationPath);
        Assert.Equal(first, a.DataGenerationId);
        Assert.Equal(second, b.DataGenerationId);
        Assert.Contains("7101", await File.ReadAllTextAsync(a.ConfigurationPath), StringComparison.Ordinal);
        Assert.Contains("7102", await File.ReadAllTextAsync(b.ConfigurationPath), StringComparison.Ordinal);
        Assert.Equal(a, await store.StageAsync(1, firstHash, CancellationToken.None, dataGenerationId: first));
        string conflicting = WriteGeneration(context, first, "mixed-port: 7103\n");
        await Assert.ThrowsAsync<MihomoGenerationConflictException>(() => store.StageAsync(1, conflicting, CancellationToken.None, dataGenerationId: first));
        Assert.Contains("7101", await File.ReadAllTextAsync(a.ConfigurationPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_DifferentDataDirectoryCannotReuseAnIdenticalRuntimeVersionAndHash()
    {
        FakeMihomoChildProcess firstProcess = new("first", 201);
        FakeMihomoChildProcess secondProcess = new("second", 202);
        await using MihomoChildSupervisorTestContext context = new([firstProcess, secondProcess], generationLayout: true);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        string hash = WriteGeneration(context, first, "mixed-port: 7101\n");
        Assert.Equal(hash, WriteGeneration(context, second, "mixed-port: 7101\n"));
        MihomoServiceCommandProcessor processor = new(context.Options, context.Supervisor, context.Logs, context.ControllerBroker);
        MihomoServiceIpcResponse started = await processor.ProcessAsync(Request(first, hash, MihomoServiceIpcCommand.Start), CancellationToken.None);
        Assert.True(started.Succeeded);
        MihomoServiceIpcControllerBinding oldBinding = Binding(started.Snapshot!);

        MihomoServiceIpcResponse reloaded = await processor.ProcessAsync(Request(second, hash, MihomoServiceIpcCommand.Reload), CancellationToken.None);

        Assert.True(reloaded.Succeeded);
        Assert.Equal(second, reloaded.Snapshot!.ActiveDataGenerationId);
        Assert.Equal(202, reloaded.Snapshot.ChildProcessId);
        Assert.True(firstProcess.StopCompleted);
        Assert.True(firstProcess.IsDisposed);
        Assert.Equal(2, context.Launcher.Requests.Count);
        Assert.Equal("service.controller.stale_generation", context.Supervisor.TryGetReadyControllerContext(oldBinding, out _));
        Assert.Null(context.Supervisor.TryGetReadyControllerContext(Binding(reloaded.Snapshot), out MihomoControllerRuntimeContext? current));
        Assert.Equal(second, current!.DataGenerationId);
        MihomoChildOperationResult stopped = await context.Supervisor.StopAsync(CancellationToken.None);
        Assert.Null(stopped.Snapshot.ActiveDataGenerationId);
        Assert.Null(stopped.Snapshot.Validate());
    }

    [Fact]
    public async Task MissingDataDirectory_DoesNotFallBackToTheInstalledLegacyConfiguration()
    {
        await using MihomoChildSupervisorTestContext context = new([], generationLayout: true);
        string legacyHash = context.WriteConfiguration("mixed-port: 7101\n");

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(1, legacyHash, CancellationToken.None, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Empty(context.Launcher.Requests);
        Assert.Equal(MihomoServiceChildState.Stopped, result.Snapshot.ChildState);
        Assert.Null(result.Snapshot.ActiveDataGenerationId);
    }

    [Fact]
    public async Task RejectedCandidateHash_PreservesTheRunningDirectoryAndChild()
    {
        FakeMihomoChildProcess process = new("retained", 201);
        await using MihomoChildSupervisorTestContext context = new([process], generationLayout: true);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        string hash = WriteGeneration(context, first, "mixed-port: 7101\n");
        _ = WriteGeneration(context, second, "mixed-port: 7102\n");
        Assert.True((await context.Supervisor.StartAsync(1, hash, CancellationToken.None, first)).Succeeded);

        MihomoChildOperationResult result = await context.Supervisor.ReloadAsync(1, hash, CancellationToken.None, second);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.configuration_hash_mismatch", result.ErrorCode);
        Assert.Equal(first, result.Snapshot.ActiveDataGenerationId);
        Assert.Equal(201, result.Snapshot.ChildProcessId);
        Assert.False(process.StopCompleted);
        Assert.Single(context.Launcher.Requests);
    }

    [Fact]
    public async Task UnexpectedRestart_KeepsTheDataIdentityAndUsesOwnedImmutableBytes()
    {
        FakeMihomoChildProcess first = new("first", 201);
        FakeMihomoChildProcess replacement = new("replacement", 202);
        await using MihomoChildSupervisorTestContext context = new([first, replacement], generationLayout: true);
        Guid identity = Guid.NewGuid();
        string hash = WriteGeneration(context, identity, "mixed-port: 7101\n");
        Assert.True((await context.Supervisor.StartAsync(1, hash, CancellationToken.None, identity)).Succeeded);
        File.Delete(SourcePath(context, identity));

        first.Exit(1);

        await MihomoServiceTestSupport.WaitUntilAsync(() => context.Supervisor.GetSnapshot().ChildProcessId == 202
            && context.Supervisor.GetSnapshot().ChildState == MihomoServiceChildState.Running);
        MihomoServiceIpcSnapshot snapshot = context.Supervisor.GetSnapshot();
        Assert.Equal(identity, snapshot.ActiveDataGenerationId);
        Assert.Equal(hash, snapshot.ActiveConfigurationHash);
        Assert.Null(context.Supervisor.TryGetReadyControllerContext(Binding(snapshot), out var controller));
        Assert.Equal(identity, controller!.DataGenerationId);
        Assert.Null(snapshot.Validate());
    }

    private static string WriteGeneration(MihomoChildSupervisorTestContext context, Guid identity, string contents)
    {
        string path = SourcePath(context, identity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string managed = MihomoServiceTestSupport.BuildManagedServiceConfiguration(contents);
        File.WriteAllText(path, managed);
        return MihomoServiceTestSupport.ComputeHash(managed);
    }

    private static string SourcePath(MihomoChildSupervisorTestContext context, Guid identity) => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(context.Options.ConfigPath))!, "Data", "v1", "generations", identity.ToString("N"), "mihomo", "config.yaml");

    private static MihomoServiceIpcRequest Request(Guid identity, string hash, MihomoServiceIpcCommand command) => new()
    {
        ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
        RequestId = Guid.NewGuid(),
        AuthenticationToken = MihomoServiceTestSupport.Token,
        Command = command,
        DataGenerationId = identity,
        Generation = 1,
        ConfigurationHash = hash,
    };

    private static MihomoServiceIpcControllerBinding Binding(MihomoServiceIpcSnapshot snapshot) => new()
    {
        ServiceSessionId = snapshot.SessionId,
        Generation = snapshot.ActiveGeneration!.Value,
        ConfigurationHash = snapshot.ActiveConfigurationHash!,
        DataGenerationId = snapshot.ActiveDataGenerationId,
    };
}
