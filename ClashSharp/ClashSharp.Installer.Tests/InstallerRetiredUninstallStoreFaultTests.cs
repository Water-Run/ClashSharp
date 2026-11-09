using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// The retired-account uninstall journal: only that account's uninstall chain may live there, bytes must be
/// canonical, and every read, write and delete boundary must fail safe and converge after a restart.
/// </summary>
public sealed class InstallerRetiredUninstallStoreFaultTests
{
    private const string OtherSid = "S-1-5-21-100-200-300-1002";

    private static readonly InjectedFault[] Faults =
        [InjectedFault.Io, InjectedFault.StateUncertain, InjectedFault.CancelAndThrow, InjectedFault.CancelOnly];

    public static TheoryData<int, InjectedFault> Cases()
    {
        var data = new TheoryData<int, InjectedFault>();
        World clean = new(plan: null);
        clean.DriveToCompletionAsync(CancellationToken.None).GetAwaiter().GetResult();
        for (int point = 0; point < clean.Plan.Points.Count; point++)
        {
            foreach (InjectedFault fault in Faults)
            {
                data.Add(point, fault);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FaultAtAnyStorageBoundaryKeepsTheJournalCanonicalAndARestartConverges(int point, InjectedFault fault)
    {
        World world = new((index, _) => index == point ? fault : null);

        Exception? failure = await Record.ExceptionAsync(() => world.DriveToCompletionAsync(world.Plan.Cancellation.Token));

        Assert.True(
            failure is null or InstallerProtocolException or InstallerStateUncertainException or OperationCanceledException,
            $"Undeclared {failure?.GetType().FullName}: {failure?.Message}");
        Assert.NotNull(world.Plan.FaultedPoint);
        // Persisted bytes are always either absent or exactly the canonical document of a valid uninstall state.
        byte[]? persisted = world.Content;
        if (persisted is not null)
        {
            InstallerTransactionJournal parsed = InstallerTransactionCodec.Parse(persisted);
            Assert.Equal(persisted, InstallerTransactionCodec.Serialize(parsed));
            Assert.Equal(InstallerOperation.Uninstall, parsed.Operation);
            Assert.Equal(InstallerTestData.Sid, parsed.TargetSid);
        }

        // A fault after the final delete only lost the acknowledgement: the uninstall is already complete.
        bool completed = world.DeletedPhases.Count > 0;
        world.DisarmFaults();
        if (!completed)
        {
            await world.DriveToCompletionAsync(CancellationToken.None);
        }

        Assert.Null(world.Content);
        // Each phase is committed exactly once, strictly forward, however often the run was interrupted,
        // and the only thing ever deleted is the Verified journal.
        Assert.Equal(World.Chain.Select(static journal => journal.Phase), world.WrittenPhases);
        Assert.Equal([InstallerTransactionPhase.Verified], world.DeletedPhases);
    }

    [Theory]
    [InlineData("other_account")]
    [InlineData("install_journal")]
    [InlineData("repair_journal")]
    [InlineData("whitespace")]
    [InlineData("bom")]
    [InlineData("garbage")]
    [InlineData("empty")]
    public async Task AForeignOrNonCanonicalJournalIsRefusedAndNeverReplaced(string defect)
    {
        World world = new(plan: null);
        InstallerTransactionJournal journal = World.Chain[0];
        byte[] bytes = defect switch
        {
            "other_account" => InstallerTransactionCodec.Serialize(journal with { TargetSid = OtherSid }),
            "install_journal" => InstallerTransactionCodec.Serialize(InstallerTestData.Journal(InstallerOperation.Install)),
            "repair_journal" => InstallerTransactionCodec.Serialize(InstallerTestData.Journal(InstallerOperation.Repair) with { AllowReassociation = false }),
            "whitespace" => Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(InstallerTransactionCodec.Serialize(journal))),
            "bom" => [.. Encoding.UTF8.GetPreamble(), .. InstallerTransactionCodec.Serialize(journal)],
            "garbage" => [0xFF, 0x00, 0x7B],
            _ => [],
        };
        world.Seed(bytes);

        InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.Store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.Store.SaveAsync(World.Chain[1], InstallerTestData.Hash, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.Store.ClearVerifiedAsync(InstallerTestData.TransactionId, InstallerTestData.Hash, CancellationToken.None));

        Assert.StartsWith("installer.", refusal.DiagnosticCode, StringComparison.Ordinal);
        Assert.Equal(bytes, world.Content);
        Assert.Empty(world.WrittenPhases);
    }

    [Fact]
    public async Task OnlyThisAccountsUninstallChainMayBeCreatedAndAdvancedAndEveryOtherShapeIsRefused()
    {
        World world = new(plan: null);
        InstallerTransactionJournal first = World.Chain[0];

        foreach (InstallerTransactionJournal refused in new[]
        {
            first with { TargetSid = OtherSid },
            InstallerTestData.Journal(InstallerOperation.Install),
            InstallerTestData.Journal(InstallerOperation.Repair),
        })
        {
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                world.Store.SaveAsync(refused, null, CancellationToken.None));
        }

        // Creation requires the Prepared generation and no expected hash.
        await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.SaveAsync(World.Chain[1], null, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.SaveAsync(first, InstallerTestData.Hash, CancellationToken.None));
        Assert.Null(world.Content);

        InstallerTransactionSnapshot created = await world.Store.SaveAsync(first, null, CancellationToken.None);
        InstallerTransactionSnapshot replay = await world.Store.SaveAsync(first, created.ContentHash, CancellationToken.None);
        Assert.Equal(created, replay);
        Assert.Single(world.WrittenPhases);

        // Skipping a phase, regressing, or carrying a stale hash is a conflict.
        AssertConflict(await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.SaveAsync(World.Chain[2], created.ContentHash, CancellationToken.None)));
        AssertConflict(await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.SaveAsync(World.Chain[1], InstallerTestData.OtherHash, CancellationToken.None)));
        InstallerTransactionSnapshot advanced = await world.Store.SaveAsync(World.Chain[1], created.ContentHash, CancellationToken.None);
        AssertConflict(await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.SaveAsync(first, advanced.ContentHash, CancellationToken.None)));
        Assert.Equal(InstallerTransactionCodec.Serialize(World.Chain[1]), world.Content);

        // The store is bound to one account: another account's store cannot see or touch this journal.
        InstallerRetiredUninstallStore otherAccount = new(OtherSid, world.Persistence);
        Assert.Equal(
            "installer.retired_uninstall.target_sid_mismatch",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() => otherAccount.LoadAsync(CancellationToken.None))).DiagnosticCode);
        Assert.Throws<InstallerProtocolException>(() => new InstallerRetiredUninstallStore("S-1-5-18", world.Persistence));
        Assert.Throws<ArgumentNullException>(() => new InstallerRetiredUninstallStore(InstallerTestData.Sid, null!));
    }

    [Fact]
    public async Task ClearRequiresTheExactVerifiedJournalAndEveryMismatchLeavesItIntact()
    {
        World world = new(plan: null);
        InstallerTransactionSnapshot snapshot = await world.Store.SaveAsync(World.Chain[0], null, CancellationToken.None);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.ClearVerifiedAsync(
            InstallerTestData.TransactionId, snapshot.ContentHash, CancellationToken.None));
        for (int index = 1; index < World.Chain.Length; index++)
        {
            snapshot = await world.Store.SaveAsync(World.Chain[index], snapshot.ContentHash, CancellationToken.None);
        }

        foreach ((string id, string hash) in new[]
        {
            (InstallerTestData.OtherHash, snapshot.ContentHash),
            (InstallerTestData.TransactionId, InstallerTestData.OtherHash),
        })
        {
            await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.ClearVerifiedAsync(id, hash, CancellationToken.None));
        }

        await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.ClearVerifiedAsync("x", snapshot.ContentHash, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.ClearVerifiedAsync(InstallerTestData.TransactionId, "y", CancellationToken.None));
        Assert.NotNull(world.Content);

        await world.Store.ClearVerifiedAsync(InstallerTestData.TransactionId, snapshot.ContentHash, CancellationToken.None);
        Assert.Null(world.Content);
        Assert.Equal(
            "installer.retired_uninstall.state_conflict",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() => world.Store.ClearVerifiedAsync(
                InstallerTestData.TransactionId, snapshot.ContentHash, CancellationToken.None))).DiagnosticCode);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    public async Task AFailingReadIsReportedWithAStableCodeAndNeverAsTheRawPlatformFailure(string kind)
    {
        World world = new(plan: null);
        world.Seed(InstallerTransactionCodec.Serialize(World.Chain[0]));
        world.ReadFailure = kind == "io"
            ? new IOException("secret-profile-path")
            : new UnauthorizedAccessException("secret-acl-detail");

        InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            world.Store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.retired_uninstall.read_failed", refusal.DiagnosticCode);
        Assert.DoesNotContain("secret", refusal.Message, StringComparison.Ordinal);
        Assert.Null(refusal.InnerException);
        world.ReadFailure = null;
        Assert.NotNull(await world.Store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OverlappingStoreCallsAreRejectedRatherThanInterleavedAndTheGuardIsAlwaysReleased()
    {
        World world = new(plan: null);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        world.ReadGate = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        Task<InstallerTransactionSnapshot?> running = world.Store.LoadAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TestTimeouts.Completion);

        foreach (Func<Task> overlapping in new Func<Task>[]
        {
            () => world.Store.LoadAsync(CancellationToken.None),
            () => world.Store.SaveAsync(World.Chain[0], null, CancellationToken.None),
            () => world.Store.ClearVerifiedAsync(InstallerTestData.TransactionId, InstallerTestData.Hash, CancellationToken.None),
        })
        {
            InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(overlapping);
            Assert.Equal("installer.retired_uninstall.concurrent_access", refusal.DiagnosticCode);
        }

        release.SetResult();
        Assert.Null(await running);
        // A refused overlapping call must not have wedged the guard for the next legitimate one.
        Assert.NotNull(await world.Store.SaveAsync(World.Chain[0], null, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostOrCancelledWriteAcknowledgementsAreResolvedFromTheStateThatActuallyPersisted(bool applied, bool cancelled)
    {
        World world = new(plan: null);
        using CancellationTokenSource cancellation = new();
        world.WriteBehavior = async token =>
        {
            if (cancelled)
            {
                await cancellation.CancelAsync();
            }

            return (applied, cancelled ? new OperationCanceledException(token) : new IOException("secret-write-detail"));
        };

        Exception? failure = await Record.ExceptionAsync(() =>
            world.Store.SaveAsync(World.Chain[0], null, cancellation.Token));

        if (applied)
        {
            // The write is proven by reading it back, so the caller never sees a failure for committed state.
            Assert.Null(failure);
            Assert.Equal(InstallerTransactionCodec.Serialize(World.Chain[0]), world.Content);
        }
        else if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Null(world.Content);
        }
        else
        {
            InstallerStateUncertainException uncertain = Assert.IsType<InstallerStateUncertainException>(failure);
            Assert.Equal("installer.retired_uninstall.write_state_uncertain", uncertain.DiagnosticCode);
            Assert.DoesNotContain("secret", uncertain.Message, StringComparison.Ordinal);
            Assert.Null(world.Content);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LostOrCancelledClearAcknowledgementsAreResolvedFromTheStateThatActuallyPersisted(bool applied, bool cancelled)
    {
        World world = new(plan: null);
        InstallerTransactionSnapshot snapshot = await world.DriveToVerifiedAsync();
        using CancellationTokenSource cancellation = new();
        world.DeleteBehavior = async token =>
        {
            if (cancelled)
            {
                await cancellation.CancelAsync();
            }

            return (applied, cancelled ? new OperationCanceledException(token) : new IOException("secret-delete-detail"));
        };

        Exception? failure = await Record.ExceptionAsync(() => world.Store.ClearVerifiedAsync(
            InstallerTestData.TransactionId, snapshot.ContentHash, cancellation.Token));

        if (applied)
        {
            Assert.Null(failure);
            Assert.Null(world.Content);
        }
        else if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.NotNull(world.Content);
        }
        else
        {
            Assert.Equal(
                "installer.retired_uninstall.clear_state_uncertain",
                Assert.IsType<InstallerStateUncertainException>(failure).DiagnosticCode);
            Assert.NotNull(world.Content);
        }
    }

    [Fact]
    public async Task AReconciliationReadThatFailsOrFindsDamageIsReportedAsUncertainNotAsSuccess()
    {
        World world = new(plan: null);
        int writes = 0;
        world.WriteBehavior = _ =>
        {
            writes++;
            return Task.FromResult<(bool, Exception?)>((true, new IOException("lost-ack")));
        };
        // The write is applied but the post-write observation fails: the store cannot claim success.
        world.AfterWrite = () => world.ReadFailure = new IOException("read-after-write failed");

        InstallerStateUncertainException uncertain = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            world.Store.SaveAsync(World.Chain[0], null, CancellationToken.None));

        Assert.Equal("installer.retired_uninstall.write_state_uncertain", uncertain.DiagnosticCode);
        Assert.Equal(1, writes);
        Assert.NotNull(world.Content);
        world.ReadFailure = null;
        // The restart observes the committed evidence and an exact replay is idempotent.
        InstallerTransactionSnapshot observed = (await world.Store.LoadAsync(CancellationToken.None))!;
        Assert.Equal(World.Chain[0], observed.Journal);
    }

    [Theory]
    [InlineData("{\"error\":\"installer.test.refused\"}", "installer.test.refused")]
    [InlineData("{\"error\":\"installer.completed\"}", "installer.completed")]
    [InlineData("{ \"error\":\"installer.test.refused\"}", "installer.retired_uninstall.document_invalid")]
    [InlineData("{\"error\":\"installer.test.refused\"} ", "installer.retired_uninstall.document_invalid")]
    [InlineData("{\"error\":\"installer.test.refused\",\"extra\":1}", "installer.transaction.json_invalid")]
    [InlineData("{\"error\":\"\\u0069nstaller.test.refused\"}", "installer.retired_uninstall.document_invalid")]
    [InlineData("{\"error\":\"Not A Code\"}", "installer.diagnostic_code_invalid")]
    [InlineData("{\"error\":\"\"}", "installer.diagnostic_code_invalid")]
    [InlineData("{\"error\":7}", "installer.transaction.json_invalid")]
    [InlineData("{\"error\":null}", "installer.transaction.json_invalid")]
    [InlineData("{\"Error\":\"installer.test.refused\"}", "installer.transaction.json_invalid")]
    [InlineData("[\"installer.test.refused\"]", "installer.transaction.json_invalid")]
    [InlineData("\"installer.test.refused\"", "installer.transaction.json_invalid")]
    [InlineData("not json", "installer.retired_uninstall.document_invalid")]
    public async Task ReadinessFramesAreEitherACanonicalSnapshotOrACanonicalStableRefusalAndNothingElse(
        string frame,
        string expectedCode)
    {
        using MemoryStream stream = new(DocumentSpecs.FrameOf(Encoding.UTF8.GetBytes(frame)));

        InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            InstallerRetiredUninstallProtocol.ReadReadyAsync(stream, CancellationToken.None));

        Assert.Equal(expectedCode, refusal.DiagnosticCode);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task AWrittenRefusalRoundTripsAndOnlyStableCodesCanBeWritten()
    {
        using MemoryStream stream = new();
        await InstallerRetiredUninstallProtocol.WriteFailureAsync(stream, "installer.test.refused", CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(
            "installer.test.refused",
            (await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InstallerRetiredUninstallProtocol.ReadReadyAsync(stream, CancellationToken.None))).DiagnosticCode);
        foreach (string unstable in new[] { "", "Has Space", "UPPER", "installer..double", ".leading", "trailing.", new string('a', 129), "ab" })
        {
            using MemoryStream sink = new();
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InstallerRetiredUninstallProtocol.WriteFailureAsync(sink, unstable, CancellationToken.None));
            Assert.Equal(0, sink.Length);
        }

        // Every durable uninstall phase maps to exactly one legal first authority command.
        foreach (InstallerTransactionJournal journal in World.Chain)
        {
            InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
            InstallerMachineHelperVerb expected = journal.Phase switch
            {
                InstallerTransactionPhase.Prepared => InstallerMachineHelperVerb.Prepare,
                InstallerTransactionPhase.MachineRemovalAuthorized => InstallerMachineHelperVerb.Remove,
                InstallerTransactionPhase.MachineCommitted => InstallerMachineHelperVerb.CommitPackage,
                _ => InstallerMachineHelperVerb.Verify,
            };
            Assert.Equal(expected, InstallerRetiredUninstallProtocol.FirstInvocation(snapshot).Verb);
        }

        foreach (InstallerTransactionJournal other in new[]
        {
            InstallerTestData.Journal(InstallerOperation.Install),
            InstallerTestData.Journal(InstallerOperation.Repair),
        })
        {
            Assert.Equal(
                "installer.retired_uninstall.operation_invalid",
                Assert.Throws<InstallerProtocolException>(() =>
                    InstallerRetiredUninstallProtocol.FirstInvocation(InstallerTransactionSnapshot.Create(other))).DiagnosticCode);
        }
    }

    private static void AssertConflict(InstallerProtocolException exception) =>
        Assert.True(
            exception.DiagnosticCode is "installer.retired_uninstall.state_conflict" or "installer.transaction.phase_transition_invalid",
            exception.DiagnosticCode);

    private sealed class World
    {
        private Func<int, string, InjectedFault?>? _decide;

        internal World(Func<int, string, InjectedFault?>? plan)
        {
            _decide = plan;
            Plan = new FaultPlan(() => 0, (index, name) => _decide?.Invoke(index, name));
            Persistence = new Persistence(this);
            Store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, Persistence);
        }

        internal static InstallerTransactionJournal[] Chain { get; } = BuildChain();

        internal FaultPlan Plan { get; }

        internal Persistence Persistence { get; }

        internal InstallerRetiredUninstallStore Store { get; private set; }

        internal byte[]? Content { get; set; }

        internal List<InstallerTransactionPhase> WrittenPhases { get; } = [];

        internal List<InstallerTransactionPhase> DeletedPhases { get; } = [];

        internal Exception? ReadFailure { get; set; }

        internal Func<Task>? ReadGate { get; set; }

        internal Func<CancellationToken, Task<(bool Applied, Exception? Failure)>>? WriteBehavior { get; set; }

        internal Func<CancellationToken, Task<(bool Applied, Exception? Failure)>>? DeleteBehavior { get; set; }

        internal Action? AfterWrite { get; set; }

        internal void Seed(byte[] bytes) => Content = bytes;

        internal void DisarmFaults() => _decide = null;

        internal async Task<InstallerTransactionSnapshot> DriveToVerifiedAsync()
        {
            InstallerTransactionSnapshot? snapshot = null;
            for (int index = 0; index < Chain.Length; index++)
            {
                snapshot = await Store.SaveAsync(Chain[index], snapshot?.ContentHash, CancellationToken.None);
            }

            return snapshot!;
        }

        /// <summary>Resumes from whatever is durable (fresh store each call) and drives to a cleared journal.</summary>
        internal async Task DriveToCompletionAsync(CancellationToken cancellationToken)
        {
            Store = new InstallerRetiredUninstallStore(InstallerTestData.Sid, Persistence);
            InstallerTransactionSnapshot? current = await Store.LoadAsync(cancellationToken);
            int next = current is null ? 0 : Array.IndexOf(Chain, Chain.First(journal => journal.Phase == current.Journal.Phase)) + 1;
            for (int index = next; index < Chain.Length; index++)
            {
                current = await Store.SaveAsync(Chain[index], current?.ContentHash, cancellationToken);
            }

            current ??= await Store.LoadAsync(cancellationToken);
            if (current is not null)
            {
                await Store.ClearVerifiedAsync(InstallerTestData.TransactionId, current.ContentHash, cancellationToken);
            }
        }

        private static InstallerTransactionJournal[] BuildChain()
        {
            InstallerTransactionJournal journal = InstallerTransactionJournal.Create(
                InstallerTestData.Request(InstallerOperation.Uninstall)) with
            {
                TransactionId = InstallerTestData.TransactionId,
            };
            List<InstallerTransactionJournal> chain = [journal];
            foreach (InstallerTransactionPhase phase in InstallerCoordinatorInterruptionTests.ExpectedChain(InstallerOperation.Uninstall).Skip(1))
            {
                journal = journal.TransitionTo(phase);
                chain.Add(journal);
            }

            return [.. chain];
        }
    }

    private sealed class Persistence(World world) : IInstallerRetiredUninstallPersistence
    {
        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            if (world.ReadGate is { } gate)
            {
                await gate();
            }

            await world.Plan.Hit("persistence.read", cancellationToken);
            if (world.ReadFailure is { } failure)
            {
                throw failure;
            }

            return world.Content is null ? null : (byte[])world.Content.Clone();
        }

        public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            await world.Plan.Hit("persistence.write.before", cancellationToken);
            if (world.WriteBehavior is { } behavior)
            {
                (bool applied, Exception? failure) = await behavior(cancellationToken);
                if (applied)
                {
                    Commit(bytes);
                }

                if (failure is not null)
                {
                    throw failure;
                }

                return;
            }

            Commit(bytes);
            await world.Plan.Hit("persistence.write.after", cancellationToken);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await world.Plan.Hit("persistence.delete.before", cancellationToken);
            if (world.DeleteBehavior is { } behavior)
            {
                (bool applied, Exception? failure) = await behavior(cancellationToken);
                if (applied)
                {
                    Remove();
                }

                if (failure is not null)
                {
                    throw failure;
                }

                return;
            }

            Remove();
            await world.Plan.Hit("persistence.delete.after", cancellationToken);
        }

        private void Commit(ReadOnlyMemory<byte> bytes)
        {
            world.Content = bytes.ToArray();
            world.WrittenPhases.Add(InstallerTransactionCodec.Parse(bytes.Span).Phase);
            world.AfterWrite?.Invoke();
        }

        private void Remove()
        {
            Assert.NotNull(world.Content);
            world.DeletedPhases.Add(InstallerTransactionCodec.Parse(world.Content).Phase);
            world.Content = null;
        }
    }
}
