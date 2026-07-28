using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies keyed import leases serialize one profile without imposing a global gate.</summary>
public sealed class ProfileImportGateTests
{
    [Fact]
    public async Task EnterAsync_SameKeyWaitsWhileDifferentKeyEntersImmediately()
    {
        ProfileImportGate gate = new();
        IDisposable first = await gate.EnterAsync("profile-a", CancellationToken.None);

        Task<IDisposable> sameProfile = gate
            .EnterAsync("profile-a", CancellationToken.None)
            .AsTask();
        Task<IDisposable> differentProfile = gate
            .EnterAsync("profile-b", CancellationToken.None)
            .AsTask();

        Assert.False(sameProfile.IsCompleted);
        using IDisposable other = await differentProfile;

        first.Dispose();
        using IDisposable second = await sameProfile;
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiterDoesNotRetainOrConsumeTheKey()
    {
        ProfileImportGate gate = new();
        using IDisposable first = await gate.EnterAsync("profile", CancellationToken.None);
        using CancellationTokenSource cancellation = new();

        Task<IDisposable> cancelled = gate
            .EnterAsync("profile", cancellation.Token)
            .AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        using IDisposable replacement = await gate.EnterAsync(
            "profile",
            CancellationToken.None);
    }
}
