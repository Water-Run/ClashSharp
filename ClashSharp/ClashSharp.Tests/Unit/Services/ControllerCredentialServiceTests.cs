using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.Security;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies private credential lifecycle without accessing the host's Windows credential slot.</summary>
public sealed class ControllerCredentialServiceTests
{
    [Fact]
    public async Task VerifiedCredential_IsReadOnlyDuringExclusiveAdmissionAndRequiresNoFurtherStorage()
    {
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        Assert.Throws<ControllerCredentialException>(service.GetSecret);
        Assert.Equal(0, store.Reads);
        using (MutationAdmissionLease lease = admission.AcquireOrdinary()) { service.InitializeAdmitted(lease, CancellationToken.None); }
        await using MutationAdmissionLease exclusive = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        store.BeforeRead = () => throw new IOException("Storage is no longer readable.");
        Assert.Equal(ControllerCredentialTestStore.ExistingSecret, service.GetSecret());
        Assert.Equal(1, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData(42)]
    public void MissingOrInvalidSlot_IsReplacedOnlyByAVerifiedCanonicalCredential(object? original)
    {
        ControllerCredentialTestStore store = new() { Present = original is not null, Value = original };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, CancellationToken.None);
        Assert.True(ControllerCredentialPolicy.IsValid(service.GetSecret()));
        Assert.Equal(store.Value, service.GetSecret());
        Assert.Equal(1, store.Writes);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public void LostWriteReply_IsResolvedFromTheDurableSlot()
    {
        ControllerCredentialTestStore store = new() { AfterWrite = () => throw new IOException("Lost write reply.") };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, CancellationToken.None);
        Assert.Equal(store.Value, service.GetSecret());
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void UnverifiedWrite_DoesNotPublishAnEphemeralCredential()
    {
        ControllerCredentialTestStore store = new() { IgnoreWrites = true };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        Assert.Equal("controller.credential.verification_failed",
            Assert.Throws<ControllerCredentialException>(() => service.InitializeAdmitted(lease, CancellationToken.None)).Code);
        Assert.Throws<ControllerCredentialException>(service.GetSecret);
        Assert.False(store.Present);
    }

    [Fact]
    public void UnavailableStorage_DoesNotFallBackToAnInMemoryCredentialOrExposePrivateErrorText()
    {
        ControllerCredentialTestStore store = new() { BeforeRead = () => throw new IOException("private-value-marker") };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        ControllerCredentialException failure = Assert.Throws<ControllerCredentialException>(() => service.InitializeAdmitted(lease, CancellationToken.None));
        Assert.Equal("controller.credential.read_failed", failure.Code);
        Assert.DoesNotContain("private-value-marker", failure.ToString(), StringComparison.Ordinal);
        Assert.Throws<ControllerCredentialException>(service.GetSecret);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void LostVerification_IsResolvedByAFreshOwnerWithoutRotatingTheCommittedCredential()
    {
        ControllerCredentialTestStore store = new();
        store.AfterWrite = () => store.BeforeRead = () => throw new IOException("Verification unavailable.");
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService first = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        Assert.Throws<ControllerCredentialException>(() => first.InitializeAdmitted(lease, CancellationToken.None));
        Assert.Throws<ControllerCredentialException>(first.GetSecret);
        object? committed = store.Value;
        store.BeforeRead = null;
        using ControllerCredentialService second = new(store, admission);
        second.InitializeAdmitted(lease, CancellationToken.None);
        Assert.Equal(committed, second.GetSecret());
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void CancellationAfterWrite_DoesNotAbandonVerification()
    {
        using CancellationTokenSource cancellation = new();
        ControllerCredentialTestStore store = new() { AfterWrite = cancellation.Cancel };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(store.Value, service.GetSecret());
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task ConcurrentInitialization_PublishesOneCredentialAndOneWrite()
    {
        ControllerCredentialTestStore store = new();
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        string[] values = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            using MutationAdmissionLease lease = admission.AcquireOrdinary();
            service.InitializeAdmitted(lease, CancellationToken.None);
            return service.GetSecret();
        })));
        Assert.Single(values.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deletion_IsVerifiedAndInvalidatesTheProcessProjectionEvenWhenItsReplyIsLost(bool loseReply)
    {
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, CancellationToken.None);
        if (loseReply) { store.AfterDelete = () => throw new IOException("Lost deletion reply."); }
        service.ClearAdmitted(lease, CancellationToken.None);
        Assert.False(store.Present);
        Assert.Throws<ControllerCredentialException>(service.GetSecret);
        service.InitializeAdmitted(lease, CancellationToken.None);
        Assert.NotEqual(ControllerCredentialTestStore.ExistingSecret, service.GetSecret());
    }

    [Fact]
    public void UnverifiedDeletion_CannotBeReportedAsClearedAndDoesNotLeaveAUsableCache()
    {
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret, IgnoreDeletes = true };
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, CancellationToken.None);
        Assert.Equal("controller.credential.delete_not_verified",
            Assert.Throws<ControllerCredentialException>(() => service.ClearAdmitted(lease, CancellationToken.None)).Code);
        Assert.True(store.Present);
        Assert.Throws<ControllerCredentialException>(service.GetSecret);
    }

    [Fact]
    public void ForeignDisposedAndCancelledAuthority_CannotAccessStorage()
    {
        ControllerCredentialTestStore store = new();
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease foreign = new MutationAdmissionBarrier().AcquireOrdinary();
        Assert.Throws<InvalidOperationException>(() => service.InitializeAdmitted(foreign, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => service.ClearAdmitted(foreign, CancellationToken.None));
        MutationAdmissionLease disposed = admission.AcquireOrdinary();
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => service.InitializeAdmitted(disposed, CancellationToken.None));
        using MutationAdmissionLease valid = admission.AcquireOrdinary();
        Assert.ThrowsAny<OperationCanceledException>(() => service.InitializeAdmitted(valid, new CancellationToken(true)));
        Assert.Equal((0, 0, 0), (store.Reads, store.Writes, store.Deletes));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public void FatalExceptionGraphs_PreserveTheirIdentity(string stage)
    {
        InvalidOperationException fatal = new("Platform wrapper.", new AggregateException(Activator.CreateInstance<OutOfMemoryException>()));
        ControllerCredentialTestStore store = new();
        MutationAdmissionBarrier admission = new();
        using ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        if (stage == "read") { store.BeforeRead = () => throw fatal; }
        if (stage == "write") { store.BeforeWrite = () => throw fatal; }
        if (stage == "delete") { store.BeforeDelete = () => throw fatal; }
        Exception observed = Assert.Throws<InvalidOperationException>(() =>
        {
            if (stage == "delete") { service.ClearAdmitted(lease, CancellationToken.None); }
            else { service.InitializeAdmitted(lease, CancellationToken.None); }
        });
        Assert.Same(fatal, observed);
    }

    [Fact]
    public void RetiredOwner_RejectsEveryOperationWithoutDeletingDurableCredentials()
    {
        ControllerCredentialTestStore store = new() { Present = true, Value = ControllerCredentialTestStore.ExistingSecret };
        MutationAdmissionBarrier admission = new();
        ControllerCredentialService service = new(store, admission);
        using MutationAdmissionLease lease = admission.AcquireOrdinary();
        service.InitializeAdmitted(lease, CancellationToken.None);
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(service.GetSecret);
        Assert.Throws<ObjectDisposedException>(() => service.InitializeAdmitted(lease, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => service.ClearAdmitted(lease, CancellationToken.None));
        Assert.True(store.Present);
        Assert.Equal(0, store.Deletes);
    }
}
