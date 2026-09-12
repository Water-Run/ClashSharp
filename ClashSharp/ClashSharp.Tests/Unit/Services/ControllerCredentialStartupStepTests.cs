using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Startup;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Checks the production startup binding with isolated credential storage.</summary>
public sealed class ControllerCredentialStartupStepTests
{
    [Fact]
    public async Task Startup_BindsOnlyTheVerifiedCredentialBeforeRuntimeRecovery()
    {
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        MihomoControllerCredentials binding = new();
        ControllerCredentialStartupStep step = new(owner, admission, binding);
        Assert.Throws<ControllerCredentialException>(binding.GetSecret);
        Assert.Equal(0, store.Reads);
        Assert.InRange(step.Order, 126, 149);
        Assert.Equal(StartupStepOutcome.Succeeded, (await step.ExecuteAsync(new AppLaunchRequest(string.Empty), CancellationToken.None)).Outcome);
        Assert.Equal(ControllerCredentialTestStore.ExistingSecret, binding.GetSecret());
        Assert.Equal(1, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Equal(StartupStepOutcome.Succeeded, (await step.ExecuteAsync(new AppLaunchRequest(string.Empty), CancellationToken.None)).Outcome);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task UnavailableCredential_PreventsStartupAndLeavesRuntimeConsumersUnbound()
    {
        ControllerCredentialTestStore store = new() { BeforeRead = () => throw new IOException("private-marker") };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        MihomoControllerCredentials binding = new();
        ControllerCredentialStartupStep step = new(owner, admission, binding);
        StartupStepResult result = await step.ExecuteAsync(new AppLaunchRequest(string.Empty), CancellationToken.None);
        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        Assert.Equal("controller.credential.read_failed", result.DiagnosticCode);
        Assert.Throws<ControllerCredentialException>(binding.GetSecret);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task CancelledStartup_NeverOpensThePrivateSlot()
    {
        ControllerCredentialTestStore store = new();
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService owner = new(store, admission);
        ControllerCredentialStartupStep step = new(owner, admission, new MihomoControllerCredentials());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(new AppLaunchRequest(string.Empty), new CancellationToken(true)));
        Assert.Equal((0, 0, 0), (store.Reads, store.Writes, store.Deletes));
    }
}
