using System.Buffers.Binary;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>Registry of every persisted or on-wire installer document format under test.</summary>
internal static class DocumentSpecs
{
    internal const string Journal = "transaction-journal";
    internal const string Command = "helper-command";
    internal const string Result = "helper-result";
    internal const string Association = "machine-association";
    internal const string Ledger = "certificate-ledger";
    internal const string OwnerTransfer = "owner-transfer-journal";
    internal const string Manifest = "release-manifest";
    internal const string TransferRequest = "owner-transfer-request-frame";
    internal const string TransferOffer = "owner-transfer-offer-frame";
    internal const string TransferDecision = "owner-transfer-decision-frame";
    internal const string RetiredSnapshot = "retired-uninstall-snapshot-frame";
    internal const string RetiredReady = "retired-uninstall-ready-frame";

    internal static string[] Names { get; } =
    [
        Journal, Command, Result, Association, Ledger, OwnerTransfer, Manifest,
        TransferRequest, TransferOffer, TransferDecision, RetiredSnapshot, RetiredReady,
    ];

    internal static DocumentSpec Get(string name) => name switch
    {
        Journal => new(
            name,
            JournalSamples,
            static bytes => InstallerTransactionCodec.Parse(bytes),
            static value => InstallerTransactionCodec.Serialize((InstallerTransactionJournal)value),
            StrictCanonical: true,
            InstallerTransactionCodec.MaximumDocumentBytes),
        Command => new(
            name,
            CommandSamples,
            static bytes => InstallerMachineHelperCommandCodec.Parse(bytes),
            static value => InstallerMachineHelperCommandCodec.Serialize((InstallerMachineHelperCommand)value),
            StrictCanonical: true,
            InstallerMachineHelperCommandCodec.MaximumCommandBytes),
        Result => new(
            name,
            ResultSamples,
            static bytes => InstallerMachineHelperResultCodec.Parse(bytes),
            static value => InstallerMachineHelperResultCodec.Serialize((InstallerMachineHelperResult)value),
            StrictCanonical: true,
            InstallerMachineHelperResultCodec.MaximumResultBytes),
        Association => new(
            name,
            static () =>
            [
                InstallerMachineAssociationCodec.Serialize(
                    InstallerMachineAssociation.Create(InstallerTestData.Sid, InstallerTestData.Hash)),
                InstallerMachineAssociationCodec.Serialize(InstallerMachineAssociation.Create(
                    "S-1-5-21-4294967295-4294967295-4294967295-65535", InstallerTestData.OtherHash)),
            ],
            static bytes => InstallerMachineAssociationCodec.Parse(bytes),
            static value => InstallerMachineAssociationCodec.Serialize((InstallerMachineAssociation)value),
            StrictCanonical: true,
            InstallerMachineAssociationCodec.MaximumAssociationBytes),
        Ledger => new(
            name,
            static () =>
            [
                InstallerCertificateOwnershipCodec.Serialize(InstallerTestData.CertificateLedger()),
                InstallerCertificateOwnershipCodec.Serialize(InstallerTestData.CertificateLedger(wasPreExisting: true)),
                InstallerCertificateOwnershipCodec.Serialize(
                    InstallerTestData.CertificateLedger().PrepareRemoval()),
            ],
            static bytes => InstallerCertificateOwnershipCodec.Parse(bytes),
            static value => InstallerCertificateOwnershipCodec.Serialize((InstallerCertificateOwnershipLedger)value),
            StrictCanonical: false,
            InstallerCertificateOwnershipCodec.MaximumDocumentBytes),
        OwnerTransfer => new(
            name,
            static () =>
            [
                InstallerOwnerTransferCodec.Serialize(InstallerOwnerTransferJournalTests.CreateJournal()),
                InstallerOwnerTransferCodec.Serialize(
                    InstallerOwnerTransferJournalTests.AtPhase(InstallerOwnerTransferPhase.Verified)),
                InstallerOwnerTransferCodec.Serialize(
                    InstallerOwnerTransferJournalTests.CreateJournal(InstallerOperation.Repair, withLedgers: true)),
            ],
            static bytes => InstallerOwnerTransferCodec.Parse(bytes),
            static value => InstallerOwnerTransferCodec.Serialize((InstallerOwnerTransferJournal)value),
            StrictCanonical: true,
            InstallerOwnerTransferCodec.MaximumDocumentBytes),
        Manifest => new(
            name,
            static () => [InstallerReleaseManifestCodec.Serialize(InstallerTestData.Manifest())],
            static bytes => InstallerReleaseManifestCodec.Parse(bytes),
            static value => InstallerReleaseManifestCodec.Serialize((InstallerReleaseManifest)value),
            StrictCanonical: false,
            InstallerPayloadBudgets.MaximumManifestBytes),
        TransferRequest => Frame(
            name,
            static () => [TransferRequestValue()],
            static (stream, value) => InstallerOwnerTransferFraming.WriteRequestAsync(
                stream, (InstallerOwnerTransferRequest)value, CancellationToken.None),
            static async stream => await InstallerOwnerTransferFraming.ReadRequestAsync(stream, CancellationToken.None),
            4096),
        TransferOffer => Frame(
            name,
            static () =>
            [
                TransferOfferValue(isRecovery: false),
                TransferOfferValue(isRecovery: true),
            ],
            static (stream, value) => InstallerOwnerTransferFraming.WriteOfferAsync(
                stream, (InstallerOwnerTransferOffer)value, CancellationToken.None),
            static async stream => await InstallerOwnerTransferFraming.ReadOfferAsync(stream, CancellationToken.None),
            4096),
        TransferDecision => Frame(
            name,
            static () =>
            [
                new InstallerOwnerTransferDecision(InstallerTestData.Hash, InstallerTestData.OtherHash, true),
                new InstallerOwnerTransferDecision(InstallerTestData.Hash, InstallerTestData.OtherHash, false),
            ],
            static (stream, value) => InstallerOwnerTransferFraming.WriteDecisionAsync(
                stream, (InstallerOwnerTransferDecision)value, CancellationToken.None),
            static async stream => await InstallerOwnerTransferFraming.ReadDecisionAsync(stream, CancellationToken.None),
            4096),
        RetiredSnapshot => Frame(
            name,
            static () =>
            [
                InstallerTransactionSnapshot.Create(InstallerTestData.Journal(InstallerOperation.Uninstall) with
                {
                    AllowReassociation = false,
                }),
            ],
            static (stream, value) => InstallerRetiredUninstallProtocol.WriteAsync(
                stream, (InstallerTransactionSnapshot)value, CancellationToken.None),
            static async stream => await InstallerRetiredUninstallProtocol.ReadAsync(stream, CancellationToken.None),
            InstallerTransactionCodec.MaximumDocumentBytes),
        RetiredReady => Frame(
            name,
            static () =>
            [
                InstallerTransactionSnapshot.Create(InstallerTestData.Journal(InstallerOperation.Uninstall) with
                {
                    AllowReassociation = false,
                }),
            ],
            static (stream, value) => InstallerRetiredUninstallProtocol.WriteAsync(
                stream, (InstallerTransactionSnapshot)value, CancellationToken.None),
            static async stream => await InstallerRetiredUninstallProtocol.ReadReadyAsync(stream, CancellationToken.None),
            InstallerTransactionCodec.MaximumDocumentBytes),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Wraps a payload in the helper frame header exactly as the stream readers expect it.</summary>
    internal static byte[] FrameOf(byte[] payload)
    {
        byte[] frame = new byte[payload.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static DocumentSpec Frame(
        string name,
        Func<object[]> values,
        Func<Stream, object, Task> write,
        Func<Stream, Task<object>> read,
        int maximumBytes) =>
        new(
            name,
            () => values().Select(value => Written(write, value)).ToArray(),
            payload => Read(read, payload),
            value => Written(write, value),
            StrictCanonical: true,
            maximumBytes);

    private static byte[] Written(Func<Stream, object, Task> write, object value)
    {
        using MemoryStream stream = new();
        write(stream, value).GetAwaiter().GetResult();
        return stream.ToArray()[sizeof(int)..];
    }

    private static object Read(Func<Stream, Task<object>> read, byte[] payload)
    {
        using MemoryStream stream = new(FrameOf(payload));
        object value = read(stream).GetAwaiter().GetResult();
        // A frame is exactly one document: nothing may be left over for the next reader.
        Assert.Equal(stream.Length, stream.Position);
        return value;
    }

    private static InstallerOwnerTransferRequest TransferRequestValue() =>
        new(InstallerTestData.Hash, InstallerOperation.Install, InstallerTestData.Version, InstallerTestData.OtherHash);

    private static InstallerOwnerTransferOffer TransferOfferValue(bool isRecovery) =>
        new(
            TransferRequestValue(),
            InstallerTestData.TransactionId,
            InstallerOwnerTransferJournalTests.PreviousSid,
            InstallerTestData.Sid,
            isRecovery);

    private static byte[][] JournalSamples()
    {
        var samples = new List<byte[]>();
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            InstallerRequest request = InstallerTestData.Request(
                operation, allowReassociation: operation == InstallerOperation.Repair);
            InstallerTransactionJournal journal = InstallerTransactionJournal.Create(request) with
            {
                TransactionId = InstallerTestData.TransactionId,
            };
            foreach (InstallerTransactionPhase phase in InstallerCoordinatorInterruptionTests.ExpectedChain(operation))
            {
                if (journal.Phase != phase)
                {
                    journal = journal.TransitionTo(phase);
                }

                samples.Add(InstallerTransactionCodec.Serialize(journal));
            }
        }

        InstallerTransactionJournal noService = InstallerTransactionJournal.Create(
            InstallerTestData.Request() with { InstallTransparentProxyService = false }) with
        {
            TransactionId = InstallerTestData.TransactionId,
        };
        samples.Add(InstallerTransactionCodec.Serialize(noService));
        InstallerTransactionJournal restoring = (InstallerTransactionJournal
            .Create(InstallerTestData.Request(InstallerOperation.Repair)) with
        {
            TransactionId = InstallerTestData.TransactionId,
        }).TransitionTo(InstallerTransactionPhase.OriginalRestored);
        samples.Add(InstallerTransactionCodec.Serialize(restoring));
        return [.. samples];
    }

    private static byte[][] CommandSamples() => VerbStates()
        .GroupBy(static pair => pair.Verb)
        .Select(static group => group.First())
        .Select(static pair => InstallerMachineHelperCommandCodec.Serialize(InstallerMachineHelperCommand.Create(
            InstallerMachineHelperInvocation.Create(pair.Verb, pair.State), pair.State)))
        .ToArray();

    /// <summary>One representative per outcome shape; the exhaustive sweep lives in its own test.</summary>
    private static byte[][] ResultSamples()
    {
        var samples = new List<byte[]>();
        InstallerMachineHelperResult? cleanup = null;
        foreach ((InstallerMachineHelperVerb verb, InstallerTransactionSnapshot state) in VerbStates())
        {
            InstallerMachineHelperCommand command = InstallerMachineHelperCommand.Create(
                InstallerMachineHelperInvocation.Create(verb, state), state);
            if (verb == InstallerMachineHelperVerb.Clear && state.Journal.Operation == InstallerOperation.Uninstall)
            {
                cleanup = InstallerMachineHelperResult.Succeeded(command, command.GetExpectedSuccessfulState()) with
                {
                    DirectoryCleanupReport = new InstallerDirectoryCleanupReport(
                        Enum.GetValues<InstallerDirectoryRole>().Select(static role => new InstallerDirectoryCleanupEntry(
                            role,
                            (InstallerDirectoryCleanupDisposition)((int)role % 4)))),
                };
            }
        }

        foreach ((InstallerMachineHelperVerb verb, string outcome) in new[]
        {
            (InstallerMachineHelperVerb.Prepare, "succeeded"),
            (InstallerMachineHelperVerb.Remove, "failed"),
            (InstallerMachineHelperVerb.Apply, "postcondition"),
            (InstallerMachineHelperVerb.ClearOriginal, "succeeded"),
            (InstallerMachineHelperVerb.RestoreOriginal, "failed"),
        })
        {
            (InstallerMachineHelperVerb _, InstallerTransactionSnapshot state) =
                VerbStates().First(pair => pair.Verb == verb);
            InstallerMachineHelperCommand command = InstallerMachineHelperCommand.Create(
                InstallerMachineHelperInvocation.Create(verb, state), state);
            InstallerTransactionSnapshot committed = command.GetExpectedSuccessfulState();
            samples.Add(InstallerMachineHelperResultCodec.Serialize(outcome switch
            {
                "succeeded" => InstallerMachineHelperResult.Succeeded(command, committed),
                "failed" => InstallerMachineHelperResult.Failed(command, "installer.test.helper_failed"),
                _ => InstallerMachineHelperResult.PostconditionFailed(command, committed, "installer.test.postcondition"),
            }));
        }

        samples.Add(InstallerMachineHelperResultCodec.Serialize(cleanup!));
        return [.. samples];
    }

    /// <summary>Every (verb, durable state) pair the invocation grammar accepts, enumerated by probing it.</summary>
    internal static IEnumerable<(InstallerMachineHelperVerb Verb, InstallerTransactionSnapshot State)> VerbStates()
    {
        foreach (InstallerMachineHelperVerb verb in Enum.GetValues<InstallerMachineHelperVerb>())
        {
            foreach (InstallerTransactionJournal journal in AllJournals())
            {
                InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
                bool legal;
                try
                {
                    InstallerMachineHelperInvocation.Create(verb, snapshot);
                    legal = true;
                }
                catch (InstallerProtocolException)
                {
                    legal = false;
                }

                if (legal)
                {
                    yield return (verb, snapshot);
                }
            }
        }
    }

    /// <summary>Every valid journal reachable by the transition rules, for every operation.</summary>
    internal static IEnumerable<InstallerTransactionJournal> AllJournals()
    {
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            foreach (bool reassociation in operation == InstallerOperation.Repair ? [true, false] : new[] { false })
            {
                InstallerTransactionJournal journal = InstallerTransactionJournal.Create(
                    InstallerTestData.Request(operation, allowReassociation: reassociation)) with
                {
                    TransactionId = InstallerTestData.TransactionId,
                };
                yield return journal;
                if (operation == InstallerOperation.Repair && !reassociation)
                {
                    yield return journal.TransitionTo(InstallerTransactionPhase.OriginalRestored);
                    InstallerTransactionJournal reserved = journal.TransitionTo(InstallerTransactionPhase.MachineReserved);
                    yield return reserved.TransitionTo(InstallerTransactionPhase.OriginalRestored);
                }

                foreach (InstallerTransactionPhase phase in InstallerCoordinatorInterruptionTests.ExpectedChain(operation).Skip(1))
                {
                    journal = journal.TransitionTo(phase);
                    yield return journal;
                }
            }
        }
    }
}
