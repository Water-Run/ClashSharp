using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Tests;

public sealed class InstallerArchivedCertificateStoreTests
{
    [Fact]
    public async Task ConstructorAndMissingReadNeverCreateOwnership()
    {
        var files = new MemoryArchive();
        var store = new InstallerArchivedCertificateStore(InstallerTestData.Sid, files);
        Assert.Equal(0, files.Reads);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            store.ReleaseReferenceAsync(Snapshot(InstallerTestData.CertificateLedger()), CancellationToken.None));
        Assert.Equal(0, files.Writes);
        Assert.Equal(0, files.Deletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CanonicalArchiveRetainsIdentityAndOnlyReleasesOneReference(bool preExisting, bool previouslyClaimed)
    {
        InstallerCertificateOwnershipLedger original = InstallerTestData.CertificateLedger(wasPreExisting: preExisting);
        if (previouslyClaimed)
        {
            original = InstallerTestData.CertificateLedger(wasPreExisting: true).TakeOwnershipForMissingCertificate();
        }
        var files = new MemoryArchive(original);
        var store = new InstallerArchivedCertificateStore(original.TargetSid, files);
        InstallerCertificateOwnershipSnapshot before = Assert.IsType<InstallerCertificateOwnershipSnapshot>(
            await store.LoadAsync(CancellationToken.None));

        InstallerCertificateOwnershipSnapshot released = await store.ReleaseReferenceAsync(before, CancellationToken.None);

        Assert.Equal(original.PrepareRemoval(), released.Ledger);
        Assert.Equal(Snapshot(released.Ledger), released);
        Assert.Equal(released, await store.ReleaseReferenceAsync(released, CancellationToken.None));
        Assert.Equal(1, files.Writes);
        await store.ClearAsync(released, CancellationToken.None);
        Assert.Null(files.Bytes);
        Assert.Equal(1, files.Deletes);
        Assert.All(files.ReturnedBuffers, bytes => Assert.All(bytes, item => Assert.Equal(0, item)));
    }

    [Theory]
    [InlineData("space")]
    [InlineData("trailing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("escaped")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("invalid-json")]
    public async Task NoncanonicalEvidenceIsRetainedAndParserDetailsAreRedacted(string condition)
    {
        string original = Encoding.UTF8.GetString(InstallerCertificateOwnershipCodec.Serialize(InstallerTestData.CertificateLedger()));
        string malformed = condition switch
        {
            "space" => " " + original,
            "trailing" => original + "\n",
            "duplicate" => original.Replace("{", "{\"schema\":1,", StringComparison.Ordinal),
            "unknown" => original.Replace("{", "{\"privatePath\":\"private-fixture\",", StringComparison.Ordinal),
            "escaped" => original.Replace("\"schema\"", "\"\\u0073chema\"", StringComparison.Ordinal),
            "empty" => "",
            "oversized" => new string('x', 4097),
            "invalid-json" => "{\"privatePath\":\"private-fixture\"",
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        var files = new MemoryArchive { Bytes = Encoding.UTF8.GetBytes(malformed) };
        byte[] expected = files.Bytes.ToArray();
        var store = new InstallerArchivedCertificateStore(InstallerTestData.Sid, files);

        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(CancellationToken.None));

        Assert.Equal("installer.certificate_archive.document_invalid", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(expected, files.Bytes);
        Assert.Equal(0, files.Writes + files.Deletes);
        Assert.All(files.ReturnedBuffers.Single(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AnotherAccountsArchiveCannotBeLoadedOrReleased()
    {
        var files = new MemoryArchive(InstallerTestData.CertificateLedger() with { TargetSid = "S-1-5-21-100-200-300-1002" });
        var store = new InstallerArchivedCertificateStore(InstallerTestData.Sid, files);
        InstallerProtocolException error = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.certificate_archive.target_sid_mismatch", error.DiagnosticCode);
        Assert.Equal(0, files.Writes + files.Deletes);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("sid")]
    [InlineData("ledger-id")]
    [InlineData("certificate")]
    [InlineData("ownership")]
    public async Task StaleOrForgedExpectedSnapshotsCannotMutateTheArchive(string condition)
    {
        InstallerCertificateOwnershipLedger original = InstallerTestData.CertificateLedger();
        var files = new MemoryArchive(original);
        var store = new InstallerArchivedCertificateStore(original.TargetSid, files);
        InstallerCertificateOwnershipSnapshot expected = condition switch
        {
            "hash" => Snapshot(original) with { ContentHash = new string('0', 64) },
            "sid" => Snapshot(original with { TargetSid = "S-1-5-21-100-200-300-1002" }),
            "ledger-id" => Snapshot(original with { LedgerId = new string('e', 64) }),
            "certificate" => Snapshot(original with { CertificateSha256 = new string('e', 64) }),
            "ownership" => Snapshot(original with { WasPreExisting = true, InstallerOwned = false }),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ReleaseReferenceAsync(expected, CancellationToken.None));

        Assert.Equal(0, files.Writes + files.Deletes);
        Assert.Equal(Snapshot(original), await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ActiveReferenceCannotBeClearedAndPreCancellationMakesNoIo()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        var files = new MemoryArchive(ledger);
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);
        await Assert.ThrowsAsync<InstallerProtocolException>(() => store.ClearAsync(Snapshot(ledger), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReleaseReferenceAsync(Snapshot(ledger), cancellation.Token));
        Assert.Equal(0, files.Reads + files.Writes + files.Deletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LostOrCancelledMutationAcknowledgementIsReconciledFromActualState(bool delete, bool cancelled)
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        if (delete)
        {
            ledger = ledger.PrepareRemoval();
        }
        using var cancellation = new CancellationTokenSource();
        var files = new MemoryArchive(ledger)
        {
            AfterMutation = _ =>
            {
                if (cancelled)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new IOException("private-fixture");
            },
        };
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);

        if (delete)
        {
            await store.ClearAsync(Snapshot(ledger), cancellation.Token);
            Assert.Null(files.Bytes);
        }
        else
        {
            Assert.Equal(ledger.PrepareRemoval(),
                (await store.ReleaseReferenceAsync(Snapshot(ledger), cancellation.Token)).Ledger);
        }
        Assert.False(files.ReadTokens.Last().CanBeCanceled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MutationNotCommittedIsNeverReportedAsSuccess(bool delete, bool cancelled)
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        if (delete)
        {
            ledger = ledger.PrepareRemoval();
        }
        using var cancellation = new CancellationTokenSource();
        var files = new MemoryArchive(ledger)
        {
            BeforeMutation = _ =>
            {
                if (cancelled)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new IOException("private-fixture");
            },
        };
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);
        Task Operation() => delete ? store.ClearAsync(Snapshot(ledger), cancellation.Token)
            : store.ReleaseReferenceAsync(Snapshot(ledger), cancellation.Token);

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Operation);
        }
        else
        {
            await Assert.ThrowsAsync<InstallerStateUncertainException>(Operation);
        }
        Assert.Equal(Snapshot(ledger), await store.LoadAsync(CancellationToken.None));
        Assert.Equal(0, files.SuccessfulMutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedEvidenceDuringReconciliationIsRetainedAsUncertain(bool delete)
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        if (delete)
        {
            ledger = ledger.PrepareRemoval();
        }
        var files = new MemoryArchive(ledger);
        byte[] conflict = InstallerCertificateOwnershipCodec.Serialize(ledger with { LedgerId = new string('d', 64) });
        files.AfterMutation = _ =>
        {
            files.Bytes = conflict.ToArray();
            return Task.CompletedTask;
        };
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);
        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => delete
            ? store.ClearAsync(Snapshot(ledger), CancellationToken.None)
            : store.ReleaseReferenceAsync(Snapshot(ledger), CancellationToken.None));
        Assert.Equal(conflict, files.Bytes);
    }

    [Fact]
    public async Task ReconciliationIsAwaitedAndOverlappingAccessIsRejectedUntilItDrains()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var files = new MemoryArchive(ledger);
        files.AfterMutation = _ =>
        {
            cancellation.Cancel();
            files.BeforeRead = async token =>
            {
                Assert.False(token.CanBeCanceled);
                entered.TrySetResult();
                await release.Task;
            };
            throw new OperationCanceledException(cancellation.Token);
        };
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);
        Task<InstallerCertificateOwnershipSnapshot> operation = store.ReleaseReferenceAsync(Snapshot(ledger), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        InstallerProtocolException overlap = await Assert.ThrowsAsync<InstallerProtocolException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Equal("installer.certificate_archive.concurrent_access", overlap.DiagnosticCode);
        release.TrySetResult();
        Assert.Equal(ledger.PrepareRemoval(), (await operation).Ledger);
        Assert.Equal(ledger.PrepareRemoval(), (await store.LoadAsync(CancellationToken.None))?.Ledger);
    }

    [Fact]
    public async Task UnreadablePostMutationStateIsUncertainWithoutPrivateExceptionDetails()
    {
        InstallerCertificateOwnershipLedger ledger = InstallerTestData.CertificateLedger();
        var files = new MemoryArchive(ledger);
        files.AfterMutation = _ =>
        {
            files.BeforeRead = _ => throw new UnauthorizedAccessException("private-fixture");
            return Task.CompletedTask;
        };
        var store = new InstallerArchivedCertificateStore(ledger.TargetSid, files);
        InstallerStateUncertainException error = await Assert.ThrowsAsync<InstallerStateUncertainException>(() =>
            store.ReleaseReferenceAsync(Snapshot(ledger), CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-fixture", error.ToString(), StringComparison.Ordinal);
        Assert.NotNull(files.Bytes);
    }

    internal static InstallerCertificateOwnershipSnapshot Snapshot(InstallerCertificateOwnershipLedger ledger) =>
        new(ledger, Convert.ToHexStringLower(SHA256.HashData(InstallerCertificateOwnershipCodec.Serialize(ledger))));

    internal sealed class MemoryArchive : IInstallerArchivedCertificatePersistence
    {
        internal MemoryArchive(InstallerCertificateOwnershipLedger? ledger = null) =>
            Bytes = ledger is null ? null : InstallerCertificateOwnershipCodec.Serialize(ledger);

        internal byte[]? Bytes { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal int SuccessfulMutations { get; private set; }
        internal List<byte[]> ReturnedBuffers { get; } = [];
        internal List<CancellationToken> ReadTokens { get; } = [];
        internal Func<CancellationToken, Task>? BeforeRead { get; set; }
        internal Func<CancellationToken, Task>? BeforeMutation { get; set; }
        internal Func<CancellationToken, Task>? AfterMutation { get; set; }

        public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            ReadTokens.Add(cancellationToken);
            if (BeforeRead is { } before)
            {
                await before(cancellationToken);
            }
            byte[]? bytes = Bytes?.ToArray();
            if (bytes is not null)
            {
                ReturnedBuffers.Add(bytes);
            }
            return bytes;
        }

        public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Writes++;
            await MutateAsync(() => Bytes = bytes.ToArray(), cancellationToken);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            Deletes++;
            await MutateAsync(() => Bytes = null, cancellationToken);
        }

        private async Task MutateAsync(Action mutation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeMutation is { } before)
            {
                await before(cancellationToken);
            }
            mutation();
            SuccessfulMutations++;
            if (AfterMutation is { } after)
            {
                await after(cancellationToken);
            }
        }
    }
}
