using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Model;

/// <summary>Tests the authenticated service-child ownership boundary.</summary>
public sealed class MihomoServiceStatusTests
{
    [Fact]
    public void HasReleasedChildOwnership_AcceptsStoppedScmOrAuthenticatedIdleHost()
    {
        Assert.True(new MihomoServiceStatus(true, false, "stopped").HasReleasedChildOwnership);
        Assert.True(CreateAuthenticatedIdleHost().HasReleasedChildOwnership);
        Assert.False(MihomoServiceStatus.Unknown("unknown").HasReleasedChildOwnership);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("session")]
    [InlineData("state")]
    [InlineData("pid")]
    [InlineData("generation")]
    [InlineData("hash")]
    public void HasReleasedChildOwnership_RejectsIncompleteOrContradictoryIdleHostProof(
        string mismatch)
    {
        MihomoServiceStatus baseline = CreateAuthenticatedIdleHost();
        MihomoServiceStatus status = mismatch switch
        {
            "protocol" => baseline with { ProtocolVersion = null },
            "session" => baseline with { ServiceSessionId = Guid.Empty },
            "state" => baseline with { ChildState = MihomoServiceChildState.Faulted },
            "pid" => baseline with { ChildProcessId = 1234 },
            "generation" => baseline with { ActiveGeneration = 7 },
            "hash" => baseline with { ActiveConfigurationHash = new string('a', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        Assert.False(status.HasReleasedChildOwnership);
    }

    private static MihomoServiceStatus CreateAuthenticatedIdleHost()
    {
        return new MihomoServiceStatus(true, false, "idle")
        {
            IsScmRunning = true,
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            ServiceSessionId = Guid.NewGuid(),
            ServiceVersion = "test",
            ChildState = MihomoServiceChildState.Stopped,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasReleasedChildOwnership_AcceptsAuthenticatedExhaustionAfterJobCleanup(bool legacyRoot)
    {
        MihomoServiceStatus status = CreateExhaustedHost();
        if (legacyRoot) { status = status with { ActiveDataGenerationId = null }; }
        Assert.True(status.HasReleasedChildOwnership);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("session")]
    [InlineData("state")]
    [InlineData("pid")]
    [InlineData("running")]
    [InlineData("unknown")]
    [InlineData("missing-generation")]
    [InlineData("missing-hash")]
    [InlineData("invalid-hash")]
    [InlineData("invalid-data-generation")]
    [InlineData("unexpected-exit")]
    [InlineData("cleanup-failed")]
    [InlineData("uninstalled")]
    [InlineData("provisioning-failed")]
    [InlineData("cleanup-uncertain")]
    public void HasReleasedChildOwnership_RejectsUnprovenExhaustedOrOtherFaultedStates(string mismatch)
    {
        MihomoServiceStatus baseline = CreateExhaustedHost();
        MihomoServiceStatus status = mismatch switch
        {
            "protocol" => baseline with { ProtocolVersion = null },
            "session" => baseline with { ServiceSessionId = Guid.Empty },
            "state" => baseline with { ChildState = MihomoServiceChildState.Starting },
            "pid" => baseline with { ChildProcessId = 1234 },
            "running" => baseline with { IsRunning = true },
            "unknown" => baseline with { ObservationState = MihomoServiceObservationState.Unknown },
            "missing-generation" => baseline with { ActiveGeneration = null },
            "missing-hash" => baseline with { ActiveConfigurationHash = null },
            "invalid-hash" => baseline with { ActiveConfigurationHash = "invalid" },
            "invalid-data-generation" => baseline with { ActiveDataGenerationId = Guid.Empty },
            "unexpected-exit" => baseline with { IpcFailureCode = "service.child.unexpected_exit" },
            "cleanup-failed" => baseline with { IpcFailureCode = "service.child.exit_cleanup_failed" },
            "uninstalled" => baseline with { IsInstalled = false },
            "provisioning-failed" => baseline with { ProvisioningFailureCode = "service.provisioning.association_invalid" },
            "cleanup-uncertain" => baseline with { CleanupFailureCode = "service.ipc.stop_failed" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        Assert.False(status.HasReleasedChildOwnership);
    }

    private static MihomoServiceStatus CreateExhaustedHost() => CreateAuthenticatedIdleHost() with
    {
        ChildState = MihomoServiceChildState.Faulted,
        ActiveGeneration = 7,
        ActiveDataGenerationId = Guid.NewGuid(),
        ActiveConfigurationHash = new string('a', 64),
        IpcFailureCode = "service.child.restart_exhausted",
    };
}
