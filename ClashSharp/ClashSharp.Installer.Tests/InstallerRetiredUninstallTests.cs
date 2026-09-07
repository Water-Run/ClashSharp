using System.Buffers.Binary;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerRetiredUninstallTests
{
    [Fact]
    public void BootstrapRoundTripsOnlyDedicatedCanonicalIdentity()
    {
        var first = InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 4242);
        var second = InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 4242);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(first, InstallerRetiredUninstallBootstrap.Parse(first.ToArguments()));
        Assert.StartsWith("ClashSharp.Installer.RetiredUninstall.", first.BuildSessionPipeName(), StringComparison.Ordinal);
        Assert.Equal(7, first.ToArguments().Count);
        Assert.Null(InstallerRetiredUninstallBootstrap.Parse([]));
        Assert.Null(InstallerRetiredUninstallBootstrap.Parse(["--machine-helper"]));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("nonce")]
    [InlineData("hash")]
    [InlineData("pid-zero")]
    [InlineData("pid-sign")]
    [InlineData("pid-padding")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("misplaced")]
    public void BootstrapRejectsAmbiguousOrUntrustedLaunchFields(string fault)
    {
        string[] args = InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 4242).ToArguments().ToArray();
        args = fault switch
        {
            "extra" => [.. args, "--target-sid"],
            "missing" => args[..^1],
            "misplaced" => ["ui", .. args],
            _ => args,
        };
        switch (fault)
        {
            case "mode": args[0] = args[0].ToUpperInvariant(); break;
            case "nonce": args[2] = "invalid"; break;
            case "hash": args[4] = InstallerTestData.Hash.ToUpperInvariant(); break;
            case "pid-zero": args[6] = "0"; break;
            case "pid-sign": args[6] = "+4242"; break;
            case "pid-padding": args[6] = "04242"; break;
            case "null": args[1] = null!; break;
        }
        Assert.Throws<InstallerProtocolException>(() => InstallerRetiredUninstallBootstrap.Parse(args));
    }

    [Theory]
    [InlineData(0, InstallerMachineHelperVerb.Prepare)]
    [InlineData(1, InstallerMachineHelperVerb.Remove)]
    [InlineData(2, InstallerMachineHelperVerb.CommitPackage)]
    [InlineData(3, InstallerMachineHelperVerb.Verify)]
    [InlineData(4, InstallerMachineHelperVerb.Verify)]
    public async Task EveryUninstallRecoveryPhaseHasAnExactBoundedReadyFrame(int phase, InstallerMachineHelperVerb verb)
    {
        InstallerTransactionSnapshot snapshot = State(phase);
        var bootstrap = InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 42);
        InstallerRetiredUninstallProtocol.ValidateAgainst(snapshot, bootstrap, InstallerTestData.Request(InstallerOperation.Uninstall));
        Assert.Equal(verb, InstallerRetiredUninstallProtocol.FirstInvocation(snapshot).Verb);
        using var stream = new MemoryStream();
        await InstallerRetiredUninstallProtocol.WriteAsync(stream, snapshot, default);
        stream.Position = 0;
        Assert.Equal(snapshot, await InstallerRetiredUninstallProtocol.ReadAsync(stream, default));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("reassociate")]
    [InlineData("account")]
    [InlineData("candidate")]
    [InlineData("version")]
    public void PublicReadyCannotChangeOperationOrAuthenticatedCandidate(string fault)
    {
        InstallerRequest request = InstallerTestData.Request(InstallerOperation.Uninstall);
        InstallerRequest changed = fault switch
        {
            "install" => request with { Operation = InstallerOperation.Install },
            "reassociate" => request with { AllowReassociation = true },
            "account" => request with { TargetSid = "S-1-5-21-100-200-300-1002" },
            "candidate" => request with { InstallerPayloadSha256 = InstallerTestData.OtherHash },
            "version" => request with { ExpectedPackageVersion = "9.0.0.0" },
            _ => throw new InvalidOperationException(),
        };
        var snapshot = State(0) with
        {
            Journal = State(0).Journal with
            {
                Operation = changed.Operation,
                AllowReassociation = changed.AllowReassociation,
                TargetSid = changed.TargetSid,
                InstallerPayloadSha256 = changed.InstallerPayloadSha256,
                ExpectedPackageVersion = changed.ExpectedPackageVersion
            }
        };
        if (fault != "reassociate")
        {
            snapshot = InstallerTransactionSnapshot.Create(snapshot.Journal);
        }
        Assert.Throws<InstallerProtocolException>(() => InstallerRetiredUninstallProtocol.ValidateAgainst(snapshot,
            InstallerRetiredUninstallBootstrap.Create(InstallerTestData.Hash, 42), request));
    }

    [Theory]
    [InlineData("space")]
    [InlineData("unknown")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public async Task ReadyRejectsNoncanonicalAndInvalidFraming(string fault)
    {
        byte[] bytes = InstallerTransactionCodec.Serialize(State(0).Journal);
        bytes = fault switch
        {
            "space" => [32, .. bytes],
            "unknown" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{", "{\"secret\":\"fixture\",", StringComparison.Ordinal)),
            "truncated" => bytes[..^1],
            "oversized" => new byte[InstallerTransactionCodec.MaximumDocumentBytes + 1],
            _ => bytes,
        };
        using var stream = new MemoryStream();
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
        stream.Position = 0;
        await Assert.ThrowsAsync<InstallerProtocolException>(() => InstallerRetiredUninstallProtocol.ReadAsync(stream, default));
    }

    [Theory]
    [InlineData("installer.retired_uninstall.current_owner")]
    [InlineData("installer.retired_uninstall.pending")]
    [InlineData("installer.retired_uninstall.candidate_mismatch")]
    public async Task AuthenticatedPreparationRefusalRoundTripsOnlyItsStableDiagnostic(string code)
    {
        using var stream = new MemoryStream();
        await InstallerRetiredUninstallProtocol.WriteFailureAsync(stream, code, default);
        stream.Position = 0;
        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() => InstallerRetiredUninstallProtocol.ReadReadyAsync(stream, default));
        Assert.Equal(code, error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.True(stream.Length < 256);
    }

    [Fact]
    public async Task PrivateJournalResumesEachPhaseAndClearsOnlyVerifiedExactState()
    {
        var files = new Persistence();
        var store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, files);
        Assert.Equal(0, files.Reads);
        Assert.Null(await store.LoadAsync(default));
        InstallerTransactionSnapshot? current = null;
        for (int phase = 0; phase < 5; phase++)
        {
            InstallerTransactionSnapshot desired = State(phase);
            current = await store.SaveAsync(desired.Journal, current?.ContentHash, default);
            Assert.Equal(desired, current);
            Assert.Equal(current, await new InstallerRetiredUninstallStore(InstallerTestData.Sid, files).LoadAsync(default));
            Assert.Equal(current, await store.SaveAsync(current.Journal, current.ContentHash, default));
        }
        Assert.Equal(5, files.Writes);
        await store.ClearVerifiedAsync(current!.Journal.TransactionId, current.ContentHash, default);
        Assert.Null(await store.LoadAsync(default));
        Assert.Equal(1, files.Deletes);
        Assert.All(files.ReadBuffers, buffer => Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("skip")]
    [InlineData("transaction")]
    [InlineData("account")]
    [InlineData("operation")]
    [InlineData("early-clear")]
    [InlineData("clear-hash")]
    [InlineData("clear-transaction")]
    public async Task ConflictingProgressNeverChangesDurableEvidence(string fault)
    {
        var files = new Persistence { Bytes = InstallerTransactionCodec.Serialize(State(0).Journal) };
        byte[] before = files.Bytes.ToArray();
        var store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, files);
        InstallerTransactionJournal next = State(1).Journal;
        if (fault == "transaction") { next = next with { TransactionId = InstallerTestData.OtherHash }; }
        if (fault == "account") { next = next with { TargetSid = "S-1-5-21-100-200-300-1002" }; }
        if (fault == "operation") { next = InstallerTestData.Journal(); }
        if (fault == "skip") { next = State(3).Journal; }
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fault.Contains("clear", StringComparison.Ordinal)
            ? store.ClearVerifiedAsync(fault == "clear-transaction" ? InstallerTestData.OtherHash : State(0).Journal.TransactionId,
                fault == "clear-hash" ? InstallerTestData.OtherHash : State(0).ContentHash, default)
            : store.SaveAsync(next, fault == "hash" ? InstallerTestData.OtherHash : State(0).ContentHash, default));
        Assert.Equal(before, files.Bytes);
        Assert.Equal(0, files.Writes + files.Deletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LostWriteOrDeleteAcknowledgementIsReconciledBeforeReportingSuccess(bool delete, bool cancelled)
    {
        var cancellation = new CancellationTokenSource();
        var files = new Persistence { Bytes = delete ? InstallerTransactionCodec.Serialize(State(4).Journal) : null };
        files.AfterMutation = () =>
        {
            if (cancelled) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw new IOException("ack lost");
        };
        var store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, files);
        if (delete)
        {
            await store.ClearVerifiedAsync(State(4).Journal.TransactionId, State(4).ContentHash, cancellation.Token);
            Assert.Null(files.Bytes);
        }
        else
        {
            Assert.Equal(State(0), await store.SaveAsync(State(0).Journal, null, cancellation.Token));
        }
        cancellation.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoncommittedCancelledMutationRetainsPriorState(bool delete)
    {
        using var cancellation = new CancellationTokenSource();
        var files = new Persistence { Bytes = delete ? InstallerTransactionCodec.Serialize(State(4).Journal) : null };
        files.BeforeMutation = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        var store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, files);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete
            ? store.ClearVerifiedAsync(State(4).Journal.TransactionId, State(4).ContentHash, cancellation.Token)
            : store.SaveAsync(State(0).Journal, null, cancellation.Token));
        Assert.Equal(delete, files.Bytes is not null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("private-fixture")]
    [InlineData(" ")]
    public async Task CorruptPrivateStateCannotBeReplacedOrLeakParserText(string text)
    {
        var files = new Persistence { Bytes = Encoding.UTF8.GetBytes(text) };
        var error = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new InstallerRetiredUninstallStore(InstallerTestData.Sid, files).LoadAsync(default));
        Assert.Equal("installer.retired_uninstall.document_invalid", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.Equal(0, files.Writes + files.Deletes);
    }

    [Fact]
    public async Task PendingReadRejectsOverlapAndCancelledCallsCannotWrite()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new Persistence { BeforeRead = () => { entered.TrySetResult(); return unblock.Task; } };
        var store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, files);
        Task<InstallerTransactionSnapshot?> pending = store.LoadAsync(default);
        try
        {
            await entered.Task;
            await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(default));
        }
        finally { unblock.TrySetResult(); await pending; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(State(0).Journal, null, new CancellationToken(true)));
        Assert.Equal(0, files.Writes);
    }

    private static InstallerTransactionSnapshot State(int phase) => InstallerTransactionSnapshot.Create(
        InstallerTestData.Journal(InstallerOperation.Uninstall, new[] { InstallerTransactionPhase.Prepared,
            InstallerTransactionPhase.MachineRemovalAuthorized, InstallerTransactionPhase.MachineCommitted,
            InstallerTransactionPhase.PackageCommitted, InstallerTransactionPhase.Verified }[phase], phase + 1));

    private sealed class Persistence : IInstallerRetiredUninstallPersistence
    {
        internal byte[]? Bytes;
        internal int Reads;
        internal int Writes;
        internal int Deletes;
        internal Action? BeforeMutation;
        internal Action? AfterMutation;
        internal Func<Task>? BeforeRead;
        internal List<byte[]> ReadBuffers { get; } = [];
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (BeforeRead is not null) { await BeforeRead(); }
            byte[]? bytes = Bytes?.ToArray();
            if (bytes is not null) { ReadBuffers.Add(bytes); }
            return bytes;
        }
        public Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeMutation?.Invoke();
            Writes++;
            Bytes = bytes.ToArray();
            AfterMutation?.Invoke();
            return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeMutation?.Invoke();
            Deletes++;
            Bytes = null;
            AfterMutation?.Invoke();
            return Task.CompletedTask;
        }
    }
}
