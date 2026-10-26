using System.Text.Json;
using ClashSharp.Agent;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Agent;

/// <summary>Checks credential readback, drained authorization changes, and failure recovery of the real runtime controller.</summary>
public sealed class McpRuntimeControllerTests
{
    [Fact]
    public async Task DisabledConfiguration_DoesNotCreateOrReadCredentials()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(new(McpServerConfiguration.DefaultPort, McpAccessPolicy.Default));
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Null(fixture.Host.BoundEndpoint);
        Assert.False(fixture.Controller.CaptureConfiguration().Access.Enabled);
    }

    [Fact]
    public async Task OrdinaryAdmission_CannotChangeAuthorizationOrRevealCredentials()
    {
        await using Fixture fixture = new();
        await using MutationAdmissionLease ordinary = await fixture.Admission.AcquireOrdinaryAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.ApplyAdmittedAsync(
            Enabled(), ordinary, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.GetClientConfigurationAdmittedAsync(
            ordinary, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.RotateCredentialAdmittedAsync(
            ordinary, CancellationToken.None));
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Host.Starts);
    }

    [Fact]
    public async Task Enable_VerifiesPersistedCredentialBeforePublishingListener()
    {
        await using Fixture fixture = new();
        fixture.Host.OnStart = () =>
        {
            Assert.Equal(1, fixture.Store.Writes);
            Assert.True(fixture.Store.Reads >= 2);
            Assert.True(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out _));
        };
        await fixture.ApplyAsync(Enabled());
        Assert.Equal(Enabled(), fixture.Controller.CaptureConfiguration());
        Assert.True(fixture.Access.HasCredential);
    }

    [Fact]
    public async Task UnobservedCredentialWrite_DoesNotStartEndpoint()
    {
        await using Fixture fixture = new();
        fixture.Store.IgnoreWrites = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ApplyAsync(Enabled()));
        Assert.Equal(0, fixture.Host.Starts);
        Assert.False(fixture.Access.Policy.Enabled);
        Assert.Null(fixture.Host.BoundEndpoint);
    }

    [Fact]
    public async Task MalformedExistingCredential_IsNotSilentlyRegenerated()
    {
        await using Fixture fixture = new();
        fixture.Store.Secret = "invalid fixture";
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.ApplyAsync(Enabled()));
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Equal(0, fixture.Host.Starts);
    }

    [Fact]
    public async Task GrantRevocation_DoesNotDependOnAnotherCredentialRead()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled(McpAccessPolicy.KnownScopes));
        int reads = fixture.Store.Reads;
        fixture.Store.ReadFailure = new IOException("storage unavailable");

        await fixture.ApplyAsync(Enabled(McpAccessScope.ReadStatus));

        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(McpAccessScope.ReadStatus, fixture.Access.Policy.Scopes);
        Assert.False(fixture.Access.Policy.Allows(McpAccessScope.ChangeProxyState));
    }

    [Fact]
    public async Task FailedPortChange_RestoresOldEndpointWithoutRestoringRevokedGrants()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled(McpAccessPolicy.KnownScopes));
        fixture.Host.RejectedPort = McpServerConfiguration.DefaultPort + 1;

        await Assert.ThrowsAsync<IOException>(() => fixture.ApplyAsync(new(fixture.Host.RejectedPort.Value,
            new(true, McpAccessScope.ReadStatus))));

        McpServerConfiguration recovered = fixture.Controller.CaptureConfiguration();
        Assert.Equal(McpServerConfiguration.DefaultPort, recovered.Port);
        Assert.Equal(McpAccessScope.ReadStatus, recovered.Access.Scopes);
        Assert.Equal(recovered.Endpoint, fixture.Host.BoundEndpoint);
    }

    [Fact]
    public async Task FailedDisable_NeverReenablesOldAuthenticationDuringRecovery()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled(McpAccessPolicy.KnownScopes));
        Assert.True(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out McpClientIdentity? oldIdentity));
        fixture.Host.StopFailures = 2;

        await Assert.ThrowsAsync<AggregateException>(() => fixture.ApplyAsync(new(McpServerConfiguration.DefaultPort,
            new(false, McpAccessScope.ReadStatus))));

        Assert.False(fixture.Access.Policy.Enabled);
        Assert.False(fixture.Access.IsCurrentIdentity(oldIdentity!));
        Assert.False(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out _));
    }

    [Fact]
    public async Task Rotation_ReplacesPersistedCredentialAndRejectsOldIdentity()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled());
        string old = fixture.Store.Secret!;
        Assert.True(fixture.Access.TryAuthenticate(old, out McpClientIdentity? oldIdentity));

        await fixture.RotateAsync();

        Assert.NotEqual(old, fixture.Store.Secret);
        Assert.False(fixture.Access.TryAuthenticate(old, out _));
        Assert.False(fixture.Access.IsCurrentIdentity(oldIdentity!));
        Assert.True(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out _));
        Assert.Equal(Enabled(), fixture.Controller.CaptureConfiguration());
    }

    [Fact]
    public async Task LostWriteAcknowledgement_AdoptsOnlyIndependentlyConfirmedReplacement()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled());
        string old = fixture.Store.Secret!;
        fixture.Store.ThrowAfterWrite = true;

        await fixture.RotateAsync();

        Assert.False(fixture.Access.TryAuthenticate(old, out _));
        Assert.True(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out _));
    }

    [Fact]
    public async Task FailedRotation_StopsAccessAndRequiresExplicitRotationRetry()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled());
        string old = fixture.Store.Secret!;
        fixture.Store.WriteFailure = new IOException("write denied");

        await Assert.ThrowsAsync<AggregateException>(() => fixture.RotateAsync());

        Assert.False(fixture.Access.TryAuthenticate(old, out _));
        Assert.Null(fixture.Host.BoundEndpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.ResumeAsync(CancellationToken.None));
        fixture.Store.WriteFailure = null;
        await fixture.RotateAsync();
        Assert.False(fixture.Access.TryAuthenticate(old, out _));
        Assert.True(fixture.Access.TryAuthenticate(fixture.Store.Secret!, out _));
    }

    [Fact]
    public async Task CopyConfiguration_CreatesVerifiedCredentialWithoutEnablingListener()
    {
        await using Fixture fixture = new();
        await using MutationAdmissionLease lease = await fixture.ExclusiveAsync();
        string connection = await fixture.Controller.GetClientConfigurationAdmittedAsync(lease, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(connection);
        Assert.Equal("Bearer " + fixture.Store.Secret, document.RootElement.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal(new McpServerConfiguration(McpServerConfiguration.DefaultPort, McpAccessPolicy.Default).Endpoint.AbsoluteUri,
            document.RootElement.GetProperty("url").GetString());
        Assert.Null(fixture.Host.BoundEndpoint);
        Assert.False(fixture.Access.Policy.Enabled);
    }

    [Fact]
    public async Task LifecyclePause_WithdrawsAccessAndResumeRetainsVerifiedUserConfiguration()
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled());
        string secret = fixture.Store.Secret!;
        await fixture.Controller.SuspendAsync(CancellationToken.None);
        Assert.False(fixture.Access.TryAuthenticate(secret, out _));
        Assert.True(fixture.Controller.CaptureSnapshot().IsSuspended);
        Assert.Throws<InvalidOperationException>(() => fixture.Controller.CaptureConfiguration());

        await fixture.Controller.ResumeAsync(CancellationToken.None);

        Assert.True(fixture.Access.TryAuthenticate(secret, out _));
        Assert.Equal(Enabled(), fixture.Controller.CaptureConfiguration());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecyclePauseFailureRestoresTheExistingEndpointCredentialAndGrants(bool callerCancels)
    {
        await using Fixture fixture = new();
        await fixture.ApplyAsync(Enabled());
        string credential = fixture.Store.Secret!;
        Uri endpoint = fixture.Host.BoundEndpoint!;
        int writes = fixture.Store.Writes;
        using var cancellation = new CancellationTokenSource();
        if (callerCancels)
        {
            fixture.Host.StopOverride = token =>
            {
                fixture.Host.StopOverride = null;
                cancellation.Cancel();
                return Task.FromCanceled(token);
            };
        }
        else { fixture.Host.StopFailures = 1; }
        var generation = new DataGenerationDescriptor(Guid.NewGuid(), 1, Path.GetTempPath());
        var participant = new McpSettingsParticipant(generation, fixture.Admission, SettingsRegistry.Default, fixture.Controller);
        var registry = new RuntimeLifetimeRegistry();
        registry.RegisterParticipant(participant);
        var lifecycle = new RuntimeLifecycleCoordinator(fixture.Admission, registry,
            networkPolicy: RuntimeShutdownNetworkPolicy.PreserveCurrentState);

        RuntimeShutdownResult result = await lifecycle.ShutdownAsync(cancellation.Token);

        Assert.Equal(RuntimeShutdownOutcome.Aborted, result.Outcome);
        Assert.Equal(callerCancels ? "shutdown-cancelled" : "quiescence-failed", result.ErrorCode);
        Assert.Empty(result.DegradedParticipants);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(fixture.Access.TryAuthenticate(credential, out _));
        Assert.Equal(endpoint, fixture.Host.BoundEndpoint);
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(credential, fixture.Store.Secret);
        Assert.Equal(Enabled(), fixture.Controller.CaptureConfiguration());
    }

    private static McpServerConfiguration Enabled(McpAccessScope scopes = McpAccessScope.ReadStatus) =>
        new(McpServerConfiguration.DefaultPort, new(true, scopes));

    private sealed class Store : IMcpCredentialStore
    {
        public string? Secret { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; set; }
        public bool ThrowAfterWrite { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadFailure is { } failure) { throw failure; }
            return Task.FromResult(Secret);
        }

        public Task WriteAsync(string secret, CancellationToken cancellationToken)
        {
            Writes++;
            if (WriteFailure is { } failure) { throw failure; }
            if (!IgnoreWrites) { Secret = secret; }
            if (ThrowAfterWrite) { throw new IOException("acknowledgement lost"); }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Host : IMcpServerHost
    {
        public Uri? BoundEndpoint { get; private set; }
        public int Starts { get; private set; }
        public int? RejectedPort { get; set; }
        public int StopFailures { get; set; }
        public Func<CancellationToken, Task>? StopOverride { get; set; }
        public Action? OnStart { get; set; }
        public Task StartAsync(McpServerConfiguration configuration, CancellationToken cancellationToken)
        {
            Starts++;
            OnStart?.Invoke();
            if (configuration.Port == RejectedPort) { throw new IOException("port unavailable"); }
            BoundEndpoint = configuration.Endpoint;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (StopOverride is { } stop) { return stop(cancellationToken); }
            if (StopFailures > 0) { StopFailures--; throw new IOException("stop unavailable"); }
            BoundEndpoint = null;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { BoundEndpoint = null; return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MutationAdmissionBarrier Admission { get; } = new();
        public Store Store { get; } = new();
        public Host Host { get; } = new();
        public McpAccessState Access { get; } = new();
        public McpRuntimeController Controller { get; }
        public Fixture() => Controller = new(Admission, Store, Host, Access);
        public ValueTask<MutationAdmissionLease> ExclusiveAsync() => Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        public async Task ApplyAsync(McpServerConfiguration configuration)
        {
            await using MutationAdmissionLease lease = await ExclusiveAsync();
            await Controller.ApplyAdmittedAsync(configuration, lease, CancellationToken.None);
        }
        public async Task RotateAsync()
        {
            await using MutationAdmissionLease lease = await ExclusiveAsync();
            await Controller.RotateCredentialAdmittedAsync(lease, CancellationToken.None);
        }
        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }
}
