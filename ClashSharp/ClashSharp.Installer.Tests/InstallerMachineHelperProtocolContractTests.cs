using System.Buffers.Binary;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// The elevated-helper wire contract: framing on a persistent stream, the verb x durable-state
/// grammar, receipt binding between commands and results, and the session guard that rejects
/// replayed, reordered, regressed or cross-session frames.
/// </summary>
public sealed class InstallerMachineHelperProtocolContractTests
{
    private const int Seed = 20261109;

    public static TheoryData<int> OversizeLengths() => new(
        0, -1, int.MinValue, InstallerMachineHelperCommandCodec.MaximumCommandBytes + 1,
        InstallerMachineHelperCommandCodec.MaximumCommandBytes * 2, 1 << 20, int.MaxValue - 1, int.MaxValue);

    public static TheoryData<InstallerMachineHelperVerb> Verbs() => new(Enum.GetValues<InstallerMachineHelperVerb>());

    public static TheoryData<int> Seeds() => new(Enumerable.Range(0, 12).ToArray());

    // ---- framing ---------------------------------------------------------------------------

    [Fact]
    public async Task TruncatedCommandAndResultStreamsFailAsEndOfStreamAtEveryByteOffset()
    {
        (InstallerMachineHelperCommand command, InstallerMachineHelperResult result) = Pair(
            InstallerMachineHelperVerb.Prepare, StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared));
        byte[] commandFrame = await FrameBytesAsync(stream => InstallerMachineHelperFraming.WriteCommandAsync(stream, command, default));
        byte[] resultFrame = await FrameBytesAsync(stream => InstallerMachineHelperFraming.WriteResultAsync(stream, result, default));

        foreach (int length in Enumerable.Range(0, commandFrame.Length))
        {
            using MemoryStream stream = new(commandFrame[..length]);
            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                InstallerMachineHelperFraming.ReadCommandAsync(stream, CancellationToken.None));
        }

        foreach (int length in Enumerable.Range(0, resultFrame.Length))
        {
            using MemoryStream stream = new(resultFrame[..length]);
            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                InstallerMachineHelperFraming.ReadResultAsync(stream, CancellationToken.None));
        }

        using MemoryStream whole = new(commandFrame);
        Assert.Equal(command, await InstallerMachineHelperFraming.ReadCommandAsync(whole, CancellationToken.None));
        Assert.Equal(whole.Length, whole.Position);
    }

    [Theory]
    [MemberData(nameof(OversizeLengths))]
    public async Task ImplausibleLengthPrefixIsRefusedWithoutAllocatingOrReadingThePayload(int declaredLength)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, declaredLength);
        foreach (bool command in new[] { true, false })
        {
            // Trailing bytes prove the reader stopped at the header rather than trusting the prefix.
            using MemoryStream stream = new([.. header, .. new byte[64]]);
            long before = GC.GetAllocatedBytesForCurrentThread();

            InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(() => command
                ? InstallerMachineHelperFraming.ReadCommandAsync(stream, CancellationToken.None)
                : InstallerMachineHelperFraming.ReadResultAsync(stream, CancellationToken.None));

            Assert.Equal("installer.machine_helper.frame_size_invalid", exception.DiagnosticCode);
            Assert.Equal(4, stream.Position);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
        }
    }

    [Fact]
    public async Task ConsecutiveFramesAreConsumedExactlyOnceInOrderAndNeverMerged()
    {
        InstallerMachineHelperCommand[] commands = VerbStatesByVerb()
            .Select(static pair => Command(pair.Verb, pair.State))
            .ToArray();
        using MemoryStream stream = new();
        foreach (InstallerMachineHelperCommand command in commands)
        {
            await InstallerMachineHelperFraming.WriteCommandAsync(stream, command, CancellationToken.None);
        }

        // A duplicated frame is delivered twice: de-duplication is the session guard's job.
        await InstallerMachineHelperFraming.WriteCommandAsync(stream, commands[0], CancellationToken.None);
        stream.Position = 0;

        foreach (InstallerMachineHelperCommand expected in commands.Append(commands[0]))
        {
            Assert.Equal(expected, await InstallerMachineHelperFraming.ReadCommandAsync(stream, CancellationToken.None));
        }

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task AFrameOfTheOtherKindIsRefusedAsAProtocolError()
    {
        (InstallerMachineHelperCommand command, InstallerMachineHelperResult result) = Pair(
            InstallerMachineHelperVerb.Prepare, StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared));
        byte[] commandFrame = await FrameBytesAsync(stream => InstallerMachineHelperFraming.WriteCommandAsync(stream, command, default));
        byte[] resultFrame = await FrameBytesAsync(stream => InstallerMachineHelperFraming.WriteResultAsync(stream, result, default));

        using MemoryStream commandAsResult = new(commandFrame);
        using MemoryStream resultAsCommand = new(resultFrame);

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            InstallerMachineHelperFraming.ReadResultAsync(commandAsResult, CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(resultAsCommand, CancellationToken.None));
    }

    [Fact]
    public async Task FramingSurvivesStreamsThatDeliverAndAcceptOneByteAtATime()
    {
        (InstallerMachineHelperCommand command, InstallerMachineHelperResult result) = Pair(
            InstallerMachineHelperVerb.Apply, StateAt(InstallerOperation.Repair, InstallerTransactionPhase.PackageCommitted));
        using TrickleStream stream = new();

        await InstallerMachineHelperFraming.WriteCommandAsync(stream, command, CancellationToken.None);
        await InstallerMachineHelperFraming.WriteResultAsync(stream, result, CancellationToken.None);
        stream.Rewind();

        Assert.True(stream.WriteCalls >= 2);
        Assert.Equal(command, await InstallerMachineHelperFraming.ReadCommandAsync(stream, CancellationToken.None));
        Assert.Equal(result, await InstallerMachineHelperFraming.ReadResultAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeAnyByteIsReadOrWritten()
    {
        (InstallerMachineHelperCommand command, InstallerMachineHelperResult result) = Pair(
            InstallerMachineHelperVerb.Prepare, StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using MemoryStream sink = new();
        using MemoryStream source = new(await FrameBytesAsync(stream =>
            InstallerMachineHelperFraming.WriteCommandAsync(stream, command, default)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallerMachineHelperFraming.WriteCommandAsync(sink, command, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallerMachineHelperFraming.WriteResultAsync(sink, result, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallerMachineHelperFraming.ReadCommandAsync(source, cancellation.Token));

        Assert.Equal(0, sink.Length);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    public async Task AStreamFailingMidFrameSurfacesTheIoFailureAndNeverReportsASuccessfulWrite(int allowedBytes)
    {
        (InstallerMachineHelperCommand command, _) = Pair(
            InstallerMachineHelperVerb.Prepare, StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared));
        using FailingWriteStream stream = new(allowedBytes);

        await Assert.ThrowsAsync<IOException>(() =>
            InstallerMachineHelperFraming.WriteCommandAsync(stream, command, CancellationToken.None));
        Assert.True(stream.Written <= allowedBytes);
    }

    [Fact]
    public async Task NullArgumentsAreRejectedBeforeTouchingTheStream()
    {
        (InstallerMachineHelperCommand command, InstallerMachineHelperResult result) = Pair(
            InstallerMachineHelperVerb.Prepare, StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared));
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            InstallerMachineHelperFraming.WriteCommandAsync(null!, command, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            InstallerMachineHelperFraming.WriteCommandAsync(stream, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            InstallerMachineHelperFraming.WriteResultAsync(stream, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            InstallerMachineHelperFraming.ReadResultAsync(null!, CancellationToken.None));
        Assert.Equal(0, stream.Length);
        _ = result;
    }

    // ---- verb x durable-state grammar -----------------------------------------------------------

    [Fact]
    public void InvocationGrammarAcceptsExactlyTheDocumentedVerbOperationPhaseTable()
    {
        int legal = 0;
        foreach (InstallerMachineHelperVerb verb in Enum.GetValues<InstallerMachineHelperVerb>())
        {
            foreach (InstallerTransactionJournal journal in DocumentSpecs.AllJournals())
            {
                InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
                bool expected = ExpectedLegal(verb, journal);
                Exception? failure = Record.Exception(() => InstallerMachineHelperInvocation.Create(verb, snapshot));

                if (expected)
                {
                    Assert.Null(failure);
                    legal++;
                }
                else
                {
                    InstallerProtocolException refusal = Assert.IsType<InstallerProtocolException>(failure);
                    Assert.Equal("installer.machine_helper.phase_invalid", refusal.DiagnosticCode);
                }
            }
        }

        Assert.True(legal >= Enum.GetValues<InstallerMachineHelperVerb>().Length);
    }

    [Fact]
    public void EveryLegalCommandHasAMonotonicExpectedStateAndRoundTripsThroughBothCodecs()
    {
        foreach ((InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) in DocumentSpecs.VerbStates())
        {
            InstallerMachineHelperCommand command = Command(verb, state);
            InstallerTransactionSnapshot expected = command.GetExpectedSuccessfulState();

            Assert.Equal(command, InstallerMachineHelperCommandCodec.Parse(InstallerMachineHelperCommandCodec.Serialize(command)));
            Assert.Equal(state, command.ToDurableState());
            Assert.Equal(state.Journal.TransactionId, expected.Journal.TransactionId);
            Assert.InRange(expected.Journal.Generation, state.Journal.Generation, state.Journal.Generation + 1);
            if (command.ClearsTerminal || verb == InstallerMachineHelperVerb.ContinueCandidate)
            {
                Assert.Equal(state.Journal.Phase, expected.Journal.Phase);
            }

            InstallerMachineHelperResult succeeded = InstallerMachineHelperResult.Succeeded(command, expected);
            InstallerMachineHelperResult failed = InstallerMachineHelperResult.Failed(command, "installer.test.helper_failed");
            Assert.Equal(succeeded, InstallerMachineHelperResultCodec.Parse(InstallerMachineHelperResultCodec.Serialize(succeeded)));
            Assert.Equal(failed, InstallerMachineHelperResultCodec.Parse(InstallerMachineHelperResultCodec.Serialize(failed)));
            Assert.Equal(expected, succeeded.ValidateAgainst(command));
            // A deterministic failure can never claim the phase advanced.
            Assert.Equal(state, failed.ValidateAgainst(command));
            Assert.False(failed.PostconditionVerified);
        }
    }

    [Fact]
    public void ResultsAreBoundToTheExactCommandSoReplayAcrossCommandsOrSessionsIsRefused()
    {
        List<(InstallerMachineHelperCommand Command, InstallerMachineHelperResult Result)> pairs = DocumentSpecs.VerbStates()
            .Select(static pair =>
            {
                InstallerMachineHelperCommand command = Command(pair.Verb, pair.State);
                return (command, InstallerMachineHelperResult.Succeeded(command, command.GetExpectedSuccessfulState()));
            })
            .ToList();
        // A second session: same shape, different random transaction identity.
        InstallerTransactionJournal other = InstallerTransactionJournal.Create(InstallerTestData.Request()) with
        {
            TransactionId = InstallerTestData.OtherHash,
        };
        InstallerMachineHelperCommand foreign = Command(
            InstallerMachineHelperVerb.Prepare, InstallerTransactionSnapshot.Create(other));
        InstallerMachineHelperResult foreignResult = InstallerMachineHelperResult.Succeeded(
            foreign, foreign.GetExpectedSuccessfulState());
        pairs.Add((foreign, foreignResult));

        foreach ((InstallerMachineHelperCommand command, InstallerMachineHelperResult result) in pairs)
        {
            foreach ((InstallerMachineHelperCommand otherCommand, _) in pairs)
            {
                if (otherCommand == command)
                {
                    Assert.Equal(command.GetExpectedSuccessfulState(), result.ValidateAgainst(otherCommand));
                    continue;
                }

                InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(
                    () => result.ValidateAgainst(otherCommand));
                Assert.StartsWith("installer.machine_helper.", refusal.DiagnosticCode, StringComparison.Ordinal);
            }

            // Tampering with any single binding field breaks validation against the true command.
            InstallerMachineHelperResult[] tampered =
            [
                result with { TransactionId = InstallerTestData.OtherHash },
                result with { JournalContentHash = InstallerTestData.OtherHash },
                result with { ResultJournalContentHash = InstallerTestData.OtherHash },
                result with { Verb = (InstallerMachineHelperVerb)(((int)result.Verb + 1) % Enum.GetValues<InstallerMachineHelperVerb>().Length) },
                result with { Outcome = InstallerMachineHelperOutcome.Failed },
                result with { PostconditionVerified = false },
                result with { DiagnosticCode = "installer.machine_helper.bad" },
                result with { Schema = 2 },
            ];
            foreach (InstallerMachineHelperResult forged in tampered.Where(candidate => candidate != result))
            {
                Assert.Throws<InstallerProtocolException>(() => forged.ValidateAgainst(command));
            }
        }
    }

    [Fact]
    public void FailedAndPostconditionFailedResultsCannotClaimSuccessOrForgeTheirState()
    {
        InstallerTransactionSnapshot state = StateAt(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted);
        InstallerMachineHelperCommand command = Command(InstallerMachineHelperVerb.Apply, state);
        InstallerTransactionSnapshot committed = command.GetExpectedSuccessfulState();

        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperResult.Failed(command, "installer.machine_helper.completed"));
        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperResult.Failed(command, "Not A Code"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerMachineHelperResult.PostconditionFailed(command, state, "installer.test.postcondition"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerMachineHelperResult.PostconditionFailed(command, committed, "installer.machine_helper.completed"));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerMachineHelperResult.Succeeded(command, state));
        InstallerMachineHelperResult postcondition = InstallerMachineHelperResult.PostconditionFailed(
            command, committed, "installer.test.postcondition");
        Assert.Equal(committed, postcondition.ValidateAgainst(command));
        Assert.Equal(InstallerMachineHelperOutcome.PostconditionFailed, postcondition.Outcome);
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void InvocationArgumentsRoundTripAndEveryDeviationFromTheGrammarIsRefused(InstallerMachineHelperVerb verb)
    {
        InstallerTransactionSnapshot state = VerbStatesByVerb().First(pair => pair.Verb == verb).State;
        InstallerMachineHelperInvocation invocation = InstallerMachineHelperInvocation.Create(verb, state);
        IReadOnlyList<string> arguments = invocation.ToArguments();

        Assert.Equal(6, arguments.Count);
        Assert.Equal(invocation, InstallerMachineHelperInvocation.Parse(arguments));

        for (int index = 0; index < arguments.Count; index++)
        {
            string[] mutated = [.. arguments];
            mutated[index] = index switch
            {
                0 => "--machine-helpe",
                1 => "unknown-verb",
                2 => "--transaction",
                3 => state.Journal.TransactionId.ToUpperInvariant(),
                4 => "--journal-hash ",
                _ => state.ContentHash[..63],
            };
            Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperInvocation.Parse(mutated));

            string[] without = [.. arguments.Where((_, i) => i != index)];
            if (index == 0)
            {
                // Without the mode flag the vector is not a helper launch at all.
                Assert.Null(InstallerMachineHelperInvocation.Parse(without));
            }
            else
            {
                Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperInvocation.Parse(without));
            }

            Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperInvocation.Parse([.. arguments, "extra"]));
        }

        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperInvocation.Parse([.. arguments.Reverse()]));
        Assert.Throws<InstallerProtocolException>(() => InstallerMachineHelperInvocation.Parse([.. arguments, null!]));
        Assert.Null(InstallerMachineHelperInvocation.Parse([]));
        Assert.Null(InstallerMachineHelperInvocation.Parse(["--elevated-not-ours", "--other"]));
        Assert.Throws<InstallerProtocolException>(() =>
            InstallerMachineHelperInvocation.Parse(["--normal", "--machine-parent-pid", "4"]));
    }

    [Fact]
    public void RandomArgumentVectorsNeverEscapeTheDeclaredExceptionAndOnlyTheExactShapeParses()
    {
        Random random = new(Seed);
        string[] vocabulary =
        [
            "--machine-helper", "--transaction-id", "--journal-hash", "prepare", "apply", "clear", "",
            InstallerTestData.TransactionId, InstallerTestData.Hash, "--machine-x", "\u0000", " ", "--MACHINE-HELPER",
        ];
        int parsed = 0;
        for (int attempt = 0; attempt < 5000; attempt++)
        {
            string[] arguments = Enumerable.Range(0, random.Next(0, 9))
                .Select(_ => vocabulary[random.Next(vocabulary.Length)])
                .ToArray();
            try
            {
                InstallerMachineHelperInvocation? invocation = InstallerMachineHelperInvocation.Parse(arguments);
                if (invocation is not null)
                {
                    parsed++;
                    Assert.Equal(arguments, invocation.ToArguments());
                }
            }
            catch (InstallerProtocolException exception)
            {
                InstallerProtocolValidation.ValidateDiagnosticCode(exception.DiagnosticCode);
            }
        }

        Assert.True(parsed >= 0);
    }

    [Fact]
    public void SessionPipeNameIsStablePerTransactionDistinctAcrossTransactionsAndLeaksNothing()
    {
        InstallerTransactionSnapshot first = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerTransactionSnapshot sameTransactionLater = StateAt(InstallerOperation.Install, InstallerTransactionPhase.PackageCommitted);
        InstallerTransactionSnapshot other = InstallerTransactionSnapshot.Create(first.Journal with
        {
            TransactionId = InstallerTestData.OtherHash,
        });

        string a = InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Prepare, first).BuildSessionPipeName();
        string b = InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Apply, sameTransactionLater).BuildSessionPipeName();
        string c = InstallerMachineHelperInvocation.Create(InstallerMachineHelperVerb.Prepare, other).BuildSessionPipeName();

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("ClashSharp.Installer.Elevation.", a, StringComparison.Ordinal);
        Assert.Equal("ClashSharp.Installer.Elevation.".Length + 32, a.Length);
        Assert.DoesNotContain(first.Journal.TransactionId, a, StringComparison.Ordinal);
        Assert.DoesNotContain(first.ContentHash, a, StringComparison.Ordinal);
        Assert.All(a["ClashSharp.Installer.Elevation.".Length..], static character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    // ---- session guard --------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GuardAgreesWithTheReplayAndPredecessorSpecificationOnRandomCommandSequences(int seed)
    {
        Random random = new(Seed + seed);
        List<InstallerMachineHelperCommand> commands = DocumentSpecs.VerbStates()
            .Select(static pair => Command(pair.Verb, pair.State))
            .ToList();
        InstallerTransactionJournal foreignJournal = InstallerTransactionJournal.Create(InstallerTestData.Request()) with
        {
            TransactionId = InstallerTestData.OtherHash,
        };
        commands.Add(Command(InstallerMachineHelperVerb.Prepare, InstallerTransactionSnapshot.Create(foreignJournal)));

        InstallerMachineHelperCommand first = commands[random.Next(commands.Count)];
        InstallerTransactionSnapshot? protectedState = first.ToDurableState();
        if (first.Verb is InstallerMachineHelperVerb.Prepare or InstallerMachineHelperVerb.Clear
                or InstallerMachineHelperVerb.ClearOriginal
            && random.Next(2) == 0)
        {
            protectedState = null;
        }

        InstallerMachineHelperSessionGuard guard = new(first.ToInvocation(), protectedState);
        bool consumed = false;
        InstallerTransactionSnapshot? latest = protectedState;
        int accepted = 0;
        int rejected = 0;
        for (int step = 0; step < 600; step++)
        {
            InstallerMachineHelperCommand command = !consumed && random.Next(2) == 0
                ? first
                : commands[random.Next(commands.Count)];
            Prediction prediction = Predict(first, consumed, latest, command);

            if (prediction.Accept)
            {
                InstallerMachineHelperSessionDisposition disposition = guard.Begin(command, latest);
                Assert.Equal(prediction.Disposition, disposition);
                InstallerTransactionSnapshot requestState = command.ToDurableState();
                InstallerTransactionSnapshot? after = disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay
                    ? latest
                    : command.ClearsTerminal ? null : command.GetExpectedSuccessfulState();
                InstallerTransactionSnapshot resultState = command.ClearsTerminal ? requestState : command.GetExpectedSuccessfulState();
                Assert.Equal(
                    resultState,
                    guard.Complete(InstallerMachineHelperResult.Succeeded(command, resultState), after));
                consumed = true;
                latest = after;
                accepted++;
            }
            else
            {
                InstallerProtocolException refusal = Assert.Throws<InstallerProtocolException>(() => guard.Begin(command, latest));
                Assert.StartsWith("installer.machine_helper.session_", refusal.DiagnosticCode, StringComparison.Ordinal);
                rejected++;
            }
        }

        Assert.True(accepted > 0 && rejected > 0);
    }

    [Fact]
    public void GuardAcceptsNothingWhileACommandIsPendingAndReconciliationReopensIt()
    {
        InstallerTransactionSnapshot prepared = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(InstallerMachineHelperVerb.Prepare, prepared);
        InstallerTransactionSnapshot reserved = prepare.GetExpectedSuccessfulState();
        InstallerMachineHelperSessionGuard guard = new(prepare.ToInvocation(), prepared);

        Assert.Equal(InstallerMachineHelperSessionDisposition.Execute, guard.Begin(prepare, prepared));
        Assert.Equal(
            "installer.machine_helper.session_command_pending",
            Assert.Throws<InstallerProtocolException>(() => guard.Begin(prepare, prepared)).DiagnosticCode);
        // A foreign or later state cannot reopen the session after an abort.
        Assert.Equal(
            "installer.machine_helper.session_abort_state_invalid",
            Assert.Throws<InstallerProtocolException>(() => guard.ReconcileAfterAbort(
                StateAt(InstallerOperation.Install, InstallerTransactionPhase.MachineCommitted))).DiagnosticCode);

        Assert.Equal(reserved, guard.ReconcileAfterAbort(reserved));
        Assert.Equal(InstallerMachineHelperSessionDisposition.VerifyCommittedReplay, guard.Begin(prepare, reserved));
        // A replay may never be aborted back to the predecessor: committed state cannot regress.
        Assert.Equal(
            "installer.machine_helper.session_abort_state_invalid",
            Assert.Throws<InstallerProtocolException>(() => guard.ReconcileAfterAbort(prepared)).DiagnosticCode);
        Assert.Equal(reserved, guard.ReconcileAfterAbort(reserved));
        Assert.Equal(
            "installer.machine_helper.session_completion_missing",
            Assert.Throws<InstallerProtocolException>(() => guard.ReconcileAfterAbort(reserved)).DiagnosticCode);
        Assert.Equal(
            "installer.machine_helper.session_completion_missing",
            Assert.Throws<InstallerProtocolException>(() => guard.Complete(
                InstallerMachineHelperResult.Succeeded(prepare, reserved), reserved)).DiagnosticCode);
    }

    [Fact]
    public void GuardRefusesAnAbortedStateOfAnotherIdentityAndAFailedResultThatMovedTheJournal()
    {
        InstallerTransactionSnapshot prepared = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(InstallerMachineHelperVerb.Prepare, prepared);
        InstallerTransactionSnapshot otherIdentity = InstallerTransactionSnapshot.Create(prepared.Journal with
        {
            TargetSid = "S-1-5-21-100-200-300-1002",
        });
        InstallerMachineHelperSessionGuard guard = new(prepare.ToInvocation(), prepared);
        guard.Begin(prepare, prepared);

        Assert.Equal(
            "installer.machine_helper.session_identity_mismatch",
            Assert.Throws<InstallerProtocolException>(() => guard.ReconcileAfterAbort(otherIdentity)).DiagnosticCode);
        InstallerMachineHelperResult failed = InstallerMachineHelperResult.Failed(prepare, "installer.test.helper_failed");
        Assert.Equal(
            "installer.machine_helper.session_protected_state_mismatch",
            Assert.Throws<InstallerProtocolException>(() => guard.Complete(failed, prepare.GetExpectedSuccessfulState())).DiagnosticCode);
        Assert.Equal(prepared, guard.Complete(failed, prepared));
        Assert.Throws<InstallerProtocolException>(() => new InstallerMachineHelperSessionGuard(
            prepare.ToInvocation(),
            InstallerTransactionSnapshot.Create(prepared.Journal with { TransactionId = InstallerTestData.OtherHash })));
    }

    // ---- authority loop over a real command/result stream -------------------------------------------

    [Fact]
    public async Task ReplayedReorderedAndCrossSessionFramesNeverChangeProtectedState()
    {
        InstallerTransactionSnapshot prepared = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(InstallerMachineHelperVerb.Prepare, prepared);
        InstallerTransactionSnapshot reserved = prepare.GetExpectedSuccessfulState();
        InstallerMachineHelperCommand commitPackage = Command(InstallerMachineHelperVerb.CommitPackage, reserved);
        InstallerTransactionSnapshot packageCommitted = commitPackage.GetExpectedSuccessfulState();
        InstallerMachineHelperCommand apply = Command(InstallerMachineHelperVerb.Apply, packageCommitted);
        InstallerTransactionSnapshot foreignState = InstallerTransactionSnapshot.Create(prepared.Journal with
        {
            TransactionId = InstallerTestData.OtherHash,
        });
        InstallerMachineHelperCommand foreign = Command(InstallerMachineHelperVerb.Prepare, foreignState);

        // Replayed: the same Prepare twice, then Apply that skipped CommitPackage (reordered), then a foreign frame.
        (List<string> events, MemoryInstallerTransactionStore store, ScriptedDuplexStream stream) =
            await RunLoopAsync(prepared, [prepare, prepare, apply, foreign], expectFailure: true);

        Assert.Equal(reserved, store.Current);
        InstallerMachineHelperResult[] replies = await ReadAllRepliesAsync(stream);
        Assert.Equal(2, replies.Length);
        Assert.All(replies, reply => Assert.Equal(reserved, reply.ValidateAgainst(prepare)));
        Assert.Equal(
            ["operation:Execute", "operation:VerifyCommittedReplay"],
            events.Where(static entry => entry.StartsWith("operation:", StringComparison.Ordinal)));
        Assert.Single(events, static entry => entry == "journal.save:MachineReserved");

        // A frame captured from another transaction is not even admitted as the first command.
        (_, MemoryInstallerTransactionStore untouched, ScriptedDuplexStream foreignStream) =
            await RunLoopAsync(prepared, [foreign], expectFailure: true, bootstrap: prepare);
        Assert.Equal(prepared, untouched.Current);
        Assert.Empty(await ReadAllRepliesAsync(foreignStream));
    }

    [Fact]
    public async Task TheLoopRepliesToEveryCommandUpToItsBoundThenRefusesWithoutLosingStateOrOrder()
    {
        InstallerTransactionSnapshot prepared = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(InstallerMachineHelperVerb.Prepare, prepared);
        InstallerMachineHelperCommand[] flood = Enumerable.Repeat(prepare, InstallerMachineHelperAuthorityLoop.MaximumCommandsPerSession + 4).ToArray();

        (_, MemoryInstallerTransactionStore store, ScriptedDuplexStream stream) =
            await RunLoopAsync(prepared, flood, expectFailure: true);

        InstallerMachineHelperResult[] replies = await ReadAllRepliesAsync(stream);
        Assert.Equal(InstallerMachineHelperAuthorityLoop.MaximumCommandsPerSession, replies.Length);
        Assert.Equal(prepare.GetExpectedSuccessfulState(), store.Current);
        Assert.Equal(
            "installer.machine_helper.command_limit_exceeded",
            Assert.IsType<InstallerProtocolException>(stream.LoopFailure).DiagnosticCode);
    }

    [Fact]
    public async Task ATerminalClearEndsTheSessionWithoutReadingAnyFurtherFrame()
    {
        InstallerTransactionSnapshot verified = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Verified);
        InstallerMachineHelperCommand clear = Command(InstallerMachineHelperVerb.Clear, verified);
        InstallerMachineHelperCommand trailing = Command(InstallerMachineHelperVerb.Verify, verified);

        (_, MemoryInstallerTransactionStore store, ScriptedDuplexStream stream) =
            await RunLoopAsync(verified, [clear, trailing], expectFailure: false);

        Assert.Null(store.Current);
        Assert.Single(await ReadAllRepliesAsync(stream));
        // The pipelined frame was left unread: nothing after a successful clear is ever executed.
        Assert.True(stream.UnreadInputBytes > 0);
    }

    [Fact]
    public async Task ATruncatedCommandFrameMidSessionFailsAsEndOfStreamAndKeepsEarlierReplies()
    {
        InstallerTransactionSnapshot prepared = StateAt(InstallerOperation.Install, InstallerTransactionPhase.Prepared);
        InstallerMachineHelperCommand prepare = Command(InstallerMachineHelperVerb.Prepare, prepared);
        InstallerTransactionSnapshot reserved = prepare.GetExpectedSuccessfulState();
        InstallerMachineHelperCommand commitPackage = Command(InstallerMachineHelperVerb.CommitPackage, reserved);
        byte[] second = await FrameBytesAsync(stream =>
            InstallerMachineHelperFraming.WriteCommandAsync(stream, commitPackage, default));

        for (int length = 0; length < second.Length; length += Math.Max(1, second.Length / 24))
        {
            (_, MemoryInstallerTransactionStore store, ScriptedDuplexStream stream) =
                await RunLoopAsync(prepared, [prepare], expectFailure: true, rawTail: second[..length]);

            Assert.IsType<EndOfStreamException>(stream.LoopFailure);
            Assert.Equal(reserved, store.Current);
            Assert.Single(await ReadAllRepliesAsync(stream));
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private readonly record struct Prediction(bool Accept, InstallerMachineHelperSessionDisposition Disposition);

    /// <summary>The documented acceptance rule: the journal is the protected state or its idempotent predecessor.</summary>
    private static Prediction Predict(
        InstallerMachineHelperCommand bootstrap,
        bool consumed,
        InstallerTransactionSnapshot? protectedState,
        InstallerMachineHelperCommand command)
    {
        Prediction reject = new(false, default);
        InstallerTransactionSnapshot request = command.ToDurableState();
        if (!consumed && command.ToInvocation() != bootstrap.ToInvocation())
        {
            return reject;
        }

        if (request.Journal.TransactionId != bootstrap.ToDurableState().Journal.TransactionId)
        {
            return reject;
        }

        if (protectedState is null)
        {
            if (command.Verb == InstallerMachineHelperVerb.Prepare && request.Journal.Phase == InstallerTransactionPhase.Prepared)
            {
                return new(true, InstallerMachineHelperSessionDisposition.Execute);
            }

            return command.ClearsTerminal
                ? new(true, InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
                : reject;
        }

        if (request.Journal.Operation != protectedState.Journal.Operation
            || request.Journal.AllowReassociation != protectedState.Journal.AllowReassociation
            || request.Journal.TargetSid != protectedState.Journal.TargetSid
            || request.Journal.InstallTransparentProxyService != protectedState.Journal.InstallTransparentProxyService)
        {
            return reject;
        }

        bool replay = !command.ClearsTerminal
            && command.Verb != InstallerMachineHelperVerb.ContinueCandidate
            && command.GetExpectedSuccessfulState() == protectedState;
        if (replay)
        {
            return new(true, InstallerMachineHelperSessionDisposition.VerifyCommittedReplay);
        }

        return request == protectedState ? new(true, InstallerMachineHelperSessionDisposition.Execute) : reject;
    }

    private static bool ExpectedLegal(InstallerMachineHelperVerb verb, InstallerTransactionJournal journal)
    {
        bool installLike = journal.Operation is InstallerOperation.Install or InstallerOperation.Repair;
        bool uninstall = journal.Operation == InstallerOperation.Uninstall;
        bool plainRepair = journal.Operation == InstallerOperation.Repair && !journal.AllowReassociation;
        InstallerTransactionPhase phase = journal.Phase;
        return verb switch
        {
            InstallerMachineHelperVerb.Prepare => phase == InstallerTransactionPhase.Prepared,
            InstallerMachineHelperVerb.CommitPackage =>
                installLike && phase == InstallerTransactionPhase.MachineReserved
                || uninstall && phase == InstallerTransactionPhase.MachineCommitted,
            InstallerMachineHelperVerb.Apply => installLike && phase == InstallerTransactionPhase.PackageCommitted,
            InstallerMachineHelperVerb.Remove => uninstall && phase == InstallerTransactionPhase.MachineRemovalAuthorized,
            InstallerMachineHelperVerb.Verify =>
                installLike && phase is InstallerTransactionPhase.MachineCommitted or InstallerTransactionPhase.Verified
                || uninstall && phase is InstallerTransactionPhase.PackageCommitted or InstallerTransactionPhase.Verified,
            InstallerMachineHelperVerb.Clear => phase == InstallerTransactionPhase.Verified,
            InstallerMachineHelperVerb.RestoreOriginal => plainRepair
                && phase is InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved
                    or InstallerTransactionPhase.OriginalRestored,
            InstallerMachineHelperVerb.ClearOriginal => plainRepair && phase == InstallerTransactionPhase.OriginalRestored,
            InstallerMachineHelperVerb.ContinueCandidate => plainRepair && phase == InstallerTransactionPhase.MachineReserved,
            _ => false,
        };
    }

    private static IEnumerable<(InstallerMachineHelperVerb Verb, InstallerTransactionSnapshot State)> VerbStatesByVerb() =>
        DocumentSpecs.VerbStates().GroupBy(static pair => pair.Verb).Select(static group => group.First());

    private static InstallerTransactionSnapshot StateAt(InstallerOperation operation, InstallerTransactionPhase phase)
    {
        InstallerTransactionJournal journal = InstallerTransactionJournal.Create(
            InstallerTestData.Request(operation, allowReassociation: operation == InstallerOperation.Repair)) with
        {
            TransactionId = InstallerTestData.TransactionId,
        };
        foreach (InstallerTransactionPhase next in InstallerCoordinatorInterruptionTests.ExpectedChain(operation).Skip(1))
        {
            if (journal.Phase == phase)
            {
                break;
            }

            journal = journal.TransitionTo(next);
        }

        return InstallerTransactionSnapshot.Create(journal);
    }

    private static InstallerMachineHelperCommand Command(InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) =>
        InstallerMachineHelperCommand.Create(InstallerMachineHelperInvocation.Create(verb, state), state);

    private static (InstallerMachineHelperCommand Command, InstallerMachineHelperResult Result) Pair(
        InstallerMachineHelperVerb verb,
        InstallerTransactionSnapshot state)
    {
        InstallerMachineHelperCommand command = Command(verb, state);
        return (command, InstallerMachineHelperResult.Succeeded(command, command.GetExpectedSuccessfulState()));
    }

    private static async Task<byte[]> FrameBytesAsync(Func<Stream, Task> write)
    {
        using MemoryStream stream = new();
        await write(stream);
        return stream.ToArray();
    }

    private static async Task<(List<string> Events, MemoryInstallerTransactionStore Store, ScriptedDuplexStream Stream)> RunLoopAsync(
        InstallerTransactionSnapshot protectedState,
        IReadOnlyList<InstallerMachineHelperCommand> commands,
        bool expectFailure,
        InstallerMachineHelperCommand? bootstrap = null,
        byte[]? rawTail = null)
    {
        List<string> events = [];
        MemoryInstallerTransactionStore store = new(events, protectedState.Journal);
        PreflightOperations operations = new(events);
        InstallerMachineHelperCommand first = bootstrap ?? commands[0];
        InstallerMachineHelperAuthoritySession session = await InstallerMachineHelperAuthoritySession.CreateAsync(
            first.ToInvocation(), store, operations, CancellationToken.None);
        ScriptedDuplexStream stream = new();
        foreach (InstallerMachineHelperCommand command in commands)
        {
            await InstallerMachineHelperFraming.WriteCommandAsync(stream.Input, command, CancellationToken.None);
        }

        if (rawTail is not null)
        {
            await stream.Input.WriteAsync(rawTail);
        }

        stream.Input.Position = 0;
        try
        {
            await InstallerMachineHelperAuthorityLoop.RunAsync(stream, session, CancellationToken.None);
            Assert.False(expectFailure, "The session was expected to be refused.");
        }
        catch (Exception exception) when (expectFailure && exception is InstallerProtocolException or EndOfStreamException)
        {
            stream.LoopFailure = exception;
        }

        return (events, store, stream);
    }

    private static async Task<InstallerMachineHelperResult[]> ReadAllRepliesAsync(ScriptedDuplexStream stream)
    {
        var replies = new List<InstallerMachineHelperResult>();
        stream.Output.Position = 0;
        while (stream.Output.Position < stream.Output.Length)
        {
            replies.Add(await InstallerMachineHelperFraming.ReadResultAsync(stream.Output, CancellationToken.None));
        }

        return [.. replies];
    }

    private sealed class PreflightOperations(List<string> events) :
        IInstallerMachineHelperOperationExecutor,
        IInstallerMachineHelperPreparationPreflight
    {
        public Task VerifyPreparationAsync(InstallerMachineHelperCommand command, CancellationToken cancellationToken)
        {
            events.Add("preflight");
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(
            InstallerMachineHelperCommand command,
            InstallerMachineHelperSessionDisposition disposition,
            CancellationToken cancellationToken)
        {
            events.Add($"operation:{disposition}");
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        internal MemoryStream Input { get; } = new();

        internal MemoryStream Output { get; } = new();

        internal Exception? LoopFailure { get; set; }

        internal long UnreadInputBytes => Input.Length - Input.Position;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => Output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => Output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Input.Dispose();
                Output.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that returns and accepts at most one byte per call, like a congested pipe.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly MemoryStream _buffer = new();

        internal int WriteCalls { get; private set; }

        internal void Rewind() => _buffer.Position = 0;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _buffer.Read(buffer, offset, Math.Min(1, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _buffer.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            for (int index = 0; index < buffer.Length; index++)
            {
                await _buffer.WriteAsync(buffer.Slice(index, 1), cancellationToken);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingWriteStream(int allowedBytes) : Stream
    {
        internal int Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Written + buffer.Length > allowedBytes)
            {
                return ValueTask.FromException(new IOException("The pipe was closed by the peer."));
            }

            Written += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
