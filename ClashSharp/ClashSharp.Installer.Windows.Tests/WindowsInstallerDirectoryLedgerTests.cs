using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerDirectoryLedgerTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramData = @"C:\ProgramData";
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void RecordCreatedIsImmutableOrderedAndIdempotentForTheExactObject()
    {
        WindowsInstallerDirectoryLedger empty = WindowsInstallerDirectoryLedger.Empty;
        WindowsInstallerDirectoryLedger first = empty.RecordCreated(InstallerDirectoryRole.AuthorityVersion, new(12, 22));
        WindowsInstallerDirectoryLedger second = first.RecordCreated(InstallerDirectoryRole.ProgramFilesProduct, new(12, 11));

        Assert.Empty(empty.Directories);
        Assert.Single(first.Directories);
        Assert.Equal(2, second.Generation);
        Assert.Equal([InstallerDirectoryRole.ProgramFilesProduct, InstallerDirectoryRole.AuthorityVersion],
            second.Directories.Select(entry => entry.Role));
        Assert.Same(second, second.RecordCreated(InstallerDirectoryRole.AuthorityVersion, new(12, 22)));

        WindowsInstallerDirectoryLedger replaced = second.RecordCreated(InstallerDirectoryRole.AuthorityVersion, new(99, 33));
        Assert.Equal(3, replaced.Generation);
        Assert.Equal(new WindowsFileIdentity(99, 33), replaced.Directories[1].Identity);
        Assert.Equal(new WindowsFileIdentity(12, 22), second.Directories[1].Identity);
        Assert.Equal(2, replaced.Directories.Count);
    }

    [Fact]
    public void ConstructorCopiesCallerEntriesAndDoesNotSortAnInvalidSequence()
    {
        WindowsInstallerOwnedDirectory[] entries = [new(InstallerDirectoryRole.InstallerRoot, new(1, 1))];
        var ledger = new WindowsInstallerDirectoryLedger(1, entries, null);
        entries[0] = new(InstallerDirectoryRole.AuthorityRoot, new(2, 2));
        Assert.Equal(InstallerDirectoryRole.InstallerRoot, ledger.Directories[0].Role);
        Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerDirectoryLedger(2,
            [entries[0], new(InstallerDirectoryRole.ProgramFilesProduct, new(1, 1))], null));
        Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerDirectoryLedger(2, [entries[0], entries[0]], null));
    }

    [Fact]
    public void InvalidIdentitiesAndGenerationAreRejectedBeforeProducingANewLedger()
    {
        WindowsInstallerDirectoryLedger empty = WindowsInstallerDirectoryLedger.Empty;
        Assert.Throws<InstallerProtocolException>(() => empty.RecordCreated((InstallerDirectoryRole)99, new(1, 1)));
        Assert.Throws<InstallerProtocolException>(() => empty.RecordCreated(InstallerDirectoryRole.InstallerRoot, new(1, 0)));
        Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerDirectoryLedger(-1, [], null));
        Assert.Throws<InstallerProtocolException>(() => new WindowsInstallerDirectoryLedger(7,
            Enumerable.Repeat(new WindowsInstallerOwnedDirectory(InstallerDirectoryRole.InstallerRoot, new(1, 1)), 7), null));
        var maximum = new WindowsInstallerDirectoryLedger(long.MaxValue, [], null);
        Assert.Throws<OverflowException>(() => maximum.RecordCreated(InstallerDirectoryRole.InstallerRoot, new(1, 1)));
        Assert.Throws<OverflowException>(() => maximum.BeginTerminal(Verified()));
        Assert.Empty(maximum.Directories);
        Assert.Null(maximum.Terminal);
    }

    [Fact]
    public void BeginTerminalIsIdempotentOnlyForTheSameCanonicalVerifiedUninstall()
    {
        WindowsInstallerDirectoryLedger created = WindowsInstallerDirectoryLedger.Empty.RecordCreated(
            InstallerDirectoryRole.InstallerRoot, new(10, 20));
        InstallerTransactionSnapshot state = Verified();
        WindowsInstallerDirectoryLedger terminal = created.BeginTerminal(state);
        Assert.Null(created.Terminal);
        Assert.Equal(created.Generation + 1, terminal.Generation);
        Assert.Equal(created.Directories, terminal.Directories);
        Assert.Same(terminal, terminal.BeginTerminal(InstallerTransactionSnapshot.Create(state.Journal)));

        foreach (InstallerTransactionJournal changed in new[]
        {
            state.Journal with { TransactionId = new string('b', 64) },
            state.Journal with { TargetSid = "S-1-5-21-100-200-300-1002" },
            state.Journal with { InstallerPayloadSha256 = new string('c', 64) },
            state.Journal with { ExpectedPackageVersion = "1.0.1.0" },
        })
        {
            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                terminal.BeginTerminal(InstallerTransactionSnapshot.Create(changed)));
            Assert.Equal("installer.directory_ledger.terminal_identity_mismatch", failure.DiagnosticCode);
        }
    }

    [Theory]
    [InlineData(InstallerOperation.Install, InstallerTransactionPhase.Verified, 5)]
    [InlineData(InstallerOperation.Repair, InstallerTransactionPhase.Verified, 5)]
    [InlineData(InstallerOperation.Uninstall, InstallerTransactionPhase.Prepared, 1)]
    public void TerminalRequiresAnIndependentlyValidVerifiedUninstall(
        InstallerOperation operation, InstallerTransactionPhase phase, int generation)
    {
        InstallerTransactionJournal journal = Verified().Journal with { Operation = operation, Phase = phase, Generation = generation };
        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerDirectoryLedger.Empty.BeginTerminal(InstallerTransactionSnapshot.Create(journal)));
        Assert.Equal("installer.directory_ledger.terminal_not_verified_uninstall", failure.DiagnosticCode);
    }

    [Fact]
    public void CodecRoundTripsAllRolesAndTheExactTerminalSnapshot()
    {
        WindowsInstallerDirectoryLedger ledger = CompleteLedger().BeginTerminal(Verified());
        byte[] serialized = WindowsInstallerDirectoryLedgerCodec.Serialize(ledger);
        WindowsInstallerDirectoryLedger parsed = WindowsInstallerDirectoryLedgerCodec.Parse(serialized);
        Assert.Equal(ledger.Generation, parsed.Generation);
        Assert.Equal(ledger.Directories, parsed.Directories);
        Assert.Equal(ledger.Terminal, parsed.Terminal);
        Assert.Equal(serialized, WindowsInstallerDirectoryLedgerCodec.Serialize(parsed));
        Assert.True(serialized.Length < WindowsInstallerDirectoryLedgerCodec.MaximumDocumentBytes);
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("property-order")]
    [InlineData("escaping")]
    public void NoncanonicalBytesAreRejectedBeforeTheyCanPoisonCompareAndSwap(string variation)
    {
        byte[] canonical = WindowsInstallerDirectoryLedgerCodec.Serialize(CompleteLedger());
        string json = Encoding.UTF8.GetString(canonical);
        string changed;
        if (variation == "property-order")
        {
            var reordered = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> property in JsonNode.Parse(canonical)!.AsObject().Reverse())
            {
                reordered.Add(property.Key, property.Value?.DeepClone());
            }
            changed = reordered.ToJsonString();
        }
        else
        {
            changed = variation == "whitespace" ? " " + json
                : json.Replace("ProgramFilesProduct", "ProgramFiles\\u0050roduct", StringComparison.Ordinal);
        }

        // Save(expected) compares Serialize(expected) to the actual file bytes. Accepting a
        // semantically equivalent, different byte sequence here would strand future updates.
        Assert.NotEqual(json, changed);
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(Utf8(changed)));
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("missing-root")]
    [InlineData("duplicate-root")]
    [InlineData("schema")]
    [InlineData("generation-string")]
    [InlineData("negative-generation")]
    [InlineData("generation-overflow")]
    [InlineData("null-entries")]
    [InlineData("unknown-entry")]
    [InlineData("duplicate-entry-property")]
    [InlineData("unknown-role")]
    [InlineData("numeric-role")]
    [InlineData("wrong-case-role")]
    [InlineData("zero-id")]
    [InlineData("negative-id")]
    [InlineData("id-overflow")]
    [InlineData("volume-overflow")]
    [InlineData("duplicate-role")]
    [InlineData("unsorted-role")]
    [InlineData("too-many-roles")]
    [InlineData("trailing-comma")]
    [InlineData("comment")]
    [InlineData("non-object")]
    [InlineData("too-deep")]
    public void CodecRejectsAmbiguousOrOutOfScopeDocuments(string mutation)
    {
        JsonObject root = JsonNode.Parse(WindowsInstallerDirectoryLedgerCodec.Serialize(CompleteLedger()))!.AsObject();
        JsonArray entries = root["createdDirectories"]!.AsArray();
        JsonObject first = entries[0]!.AsObject();
        byte[]? raw = null;
        switch (mutation)
        {
            case "unknown-root": root["extra"] = true; break;
            case "missing-root": root.Remove("terminal"); break;
            case "duplicate-root": raw = Utf8(root.ToJsonString().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)); break;
            case "schema": root["schema"] = 2; break;
            case "generation-string": root["generation"] = "6"; break;
            case "negative-generation": root["generation"] = -1; break;
            case "generation-overflow": root["generation"] = ulong.MaxValue; break;
            case "null-entries": root["createdDirectories"] = null; break;
            case "unknown-entry": first["path"] = @"C:\foreign"; break;
            case "duplicate-entry-property": raw = Utf8(root.ToJsonString().Replace("\"fileIndex\":1", "\"fileIndex\":1,\"fileIndex\":1", StringComparison.Ordinal)); break;
            case "unknown-role": first["role"] = "ForeignRoot"; break;
            case "numeric-role": first["role"] = "0"; break;
            case "wrong-case-role": first["role"] = "programFilesProduct"; break;
            case "zero-id": first["fileIndex"] = 0; break;
            case "negative-id": first["fileIndex"] = -1; break;
            case "id-overflow": first["fileIndex"] = JsonNode.Parse("18446744073709551616"); break;
            case "volume-overflow": first["volumeSerialNumber"] = ulong.MaxValue; break;
            case "duplicate-role": entries[1] = first.DeepClone(); break;
            case "unsorted-role":
                JsonNode copy = first.DeepClone();
                entries[0] = entries[1]!.DeepClone();
                entries[1] = copy;
                break;
            case "too-many-roles": entries.Add(first.DeepClone()); break;
            case "trailing-comma": raw = Utf8(root.ToJsonString()[..^1] + ",}"); break;
            case "comment": raw = Utf8("/*metadata*/" + root.ToJsonString()); break;
            case "non-object": raw = Utf8("[]"); break;
            case "too-deep": root["terminal"] = JsonNode.Parse("{\"nested\":{\"a\":{\"b\":{\"c\":1}}}}"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(raw ?? Utf8(root.ToJsonString())));
    }

    [Theory]
    [InlineData("wrong-hash")]
    [InlineData("uppercase-hash")]
    [InlineData("invalid-base64")]
    [InlineData("noncanonical-base64")]
    [InlineData("noncanonical-journal")]
    [InlineData("invalid-nonce")]
    [InlineData("uppercase-nonce")]
    [InlineData("wrong-phase")]
    [InlineData("wrong-operation")]
    [InlineData("extra-terminal-property")]
    [InlineData("duplicate-terminal-property")]
    public void TerminalBytesMustMatchTheirCanonicalNoncePhaseAndHash(string mutation)
    {
        JsonObject root = JsonNode.Parse(WindowsInstallerDirectoryLedgerCodec.Serialize(
            CompleteLedger().BeginTerminal(Verified())))!.AsObject();
        JsonObject terminal = root["terminal"]!.AsObject();
        byte[]? raw = null;
        switch (mutation)
        {
            case "wrong-hash": terminal["contentHash"] = new string('0', 64); break;
            case "uppercase-hash": terminal["contentHash"] = terminal["contentHash"]!.GetValue<string>().ToUpperInvariant(); break;
            case "invalid-base64": terminal["journalBase64"] = "%%%"; break;
            case "noncanonical-base64": terminal["journalBase64"] = " " + terminal["journalBase64"]!.GetValue<string>(); break;
            case "extra-terminal-property": terminal["path"] = @"C:\foreign"; break;
            case "duplicate-terminal-property":
                raw = Utf8(root.ToJsonString().Replace("\"contentHash\":", "\"contentHash\":\"ignored\",\"contentHash\":", StringComparison.Ordinal));
                break;
            default:
                JsonObject journal = JsonNode.Parse(Convert.FromBase64String(terminal["journalBase64"]!.GetValue<string>()))!.AsObject();
                switch (mutation)
                {
                    case "invalid-nonce": journal["transactionId"] = new string('a', 63); break;
                    case "uppercase-nonce": journal["transactionId"] = new string('A', 64); break;
                    case "wrong-phase": journal["phase"] = "prepared"; journal["generation"] = 1; break;
                    case "wrong-operation": journal["operation"] = "install"; break;
                    case "noncanonical-journal": break;
                    default: throw new ArgumentOutOfRangeException(nameof(mutation));
                }
                byte[] bytes = Utf8((mutation == "noncanonical-journal" ? " " : string.Empty) + journal.ToJsonString());
                terminal["journalBase64"] = Convert.ToBase64String(bytes);
                terminal["contentHash"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                break;
        }
        Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(raw ?? Utf8(root.ToJsonString())));
    }

    [Fact]
    public void DocumentSizeBoundaryIsCheckedBeforeJsonParsing()
    {
        int maximum = WindowsInstallerDirectoryLedgerCodec.MaximumDocumentBytes;
        byte[] padded = Enumerable.Repeat((byte)' ', maximum).ToArray();
        WindowsInstallerDirectoryLedgerCodec.Serialize(WindowsInstallerDirectoryLedger.Empty).CopyTo(padded, 0);
        InstallerProtocolException boundaryFailure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsInstallerDirectoryLedgerCodec.Parse(padded));
        Assert.NotEqual("installer.directory_ledger.document_size_invalid", boundaryFailure.DiagnosticCode);
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[maximum + 1] })
        {
            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() => WindowsInstallerDirectoryLedgerCodec.Parse(invalid));
            Assert.Equal("installer.directory_ledger.document_size_invalid", failure.DiagnosticCode);
        }
    }

    [Fact]
    public void FixedLayoutKeepsItsLedgerOutsideAllDeletionTargetsAndOrdersChildrenFirst()
    {
        var layout = new WindowsInstallerDirectoryCleanupLayout(ProgramFiles, ProgramData);
        Assert.Equal(Path.Combine(ProgramData, WindowsInstallerDirectoryCleanupLayout.LedgerFileName), layout.LedgerPath);
        InstallerDirectoryRole[] order = WindowsInstallerDirectoryCleanupLayout.DeletionOrder;
        Assert.Equal(6, order.Distinct().Count());
        foreach (InstallerDirectoryRole role in Enum.GetValues<InstallerDirectoryRole>())
        {
            string path = layout.GetPath(role);
            Assert.True(layout.TryGetRole(path.ToUpperInvariant(), out InstallerDirectoryRole observed));
            Assert.Equal(role, observed);
            Assert.False(layout.LedgerPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            foreach (InstallerDirectoryRole parent in order.Where(parent =>
                path.StartsWith(layout.GetPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                Assert.True(Array.IndexOf(order, role) < Array.IndexOf(order, parent));
            }
        }
        Assert.Throws<InstallerProtocolException>(() => layout.GetPath((InstallerDirectoryRole)99));
    }

    [Theory]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\ProgramData\ClashSharp-other")]
    [InlineData(@"C:\ProgramData\ClashSharp\Installer\v2\child")]
    [InlineData(@"C:\ProgramData\ClashSharp\Installer\v2\..")]
    [InlineData(@"C:\ProgramData\ClashSharp\Installer\v2:stream")]
    [InlineData(@"C:\ProgramData\ClashSharp\Installer\v2\")]
    [InlineData(@"\\?\C:\ProgramData\ClashSharp\Installer\v2")]
    public void FixedLayoutDoesNotAuthorizeAncestorSiblingChildOrAliasPaths(string path)
    {
        var layout = new WindowsInstallerDirectoryCleanupLayout(ProgramFiles, ProgramData);
        Assert.False(layout.TryGetRole(path, out _));
    }

    [Fact]
    public async Task PersistenceUsesOnlyTheFixedLedgerAndConfirmsSaveAndDeleteWhileAnchored()
    {
        var fixture = new PersistenceFixture();
        WindowsInstallerDirectoryLedger desired = CompleteLedger();
        Assert.Null(await fixture.Persistence.LoadAsync(CancellationToken.None));
        await fixture.Persistence.SaveAsync(null, desired, CancellationToken.None);
        WindowsInstallerDirectoryLedger? loaded = await fixture.Persistence.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(WindowsInstallerDirectoryLedgerCodec.Serialize(desired), WindowsInstallerDirectoryLedgerCodec.Serialize(loaded));
        await fixture.Persistence.DeleteAsync(desired, CancellationToken.None);
        Assert.Null(fixture.Files.Bytes);
        Assert.Equal(0, fixture.ActiveAnchors);
        Assert.All(fixture.Files.Paths, path => Assert.Equal(Path.Combine(ProgramData, WindowsInstallerDirectoryCleanupLayout.LedgerFileName), path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAcknowledgementWithoutItsPostconditionCannotSucceed(bool deleting)
    {
        var fixture = new PersistenceFixture();
        WindowsInstallerDirectoryLedger desired = CompleteLedger();
        fixture.Files.IgnoreMutation = true;
        if (deleting) { fixture.Files.Bytes = WindowsInstallerDirectoryLedgerCodec.Serialize(desired); }
        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => deleting
            ? fixture.Persistence.DeleteAsync(desired, CancellationToken.None)
            : fixture.Persistence.SaveAsync(null, desired, CancellationToken.None));
        Assert.Equal(deleting ? "installer.directory_ledger.delete_not_observed" : "installer.directory_ledger.write_not_observed", failure.DiagnosticCode);
        Assert.Equal(0, fixture.ActiveAnchors);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task StorageFailuresPropagateWithoutLosingTheAnchorLease(string operation)
    {
        var fixture = new PersistenceFixture();
        var failure = new IOException("Injected metadata I/O failure.");
        fixture.Files.Failure = failure;
        IOException observed = await Assert.ThrowsAsync<IOException>(() => RunPersistenceAsync(fixture, operation));
        Assert.Same(failure, observed);
        Assert.Equal(0, fixture.ActiveAnchors);
    }

    [Fact]
    public async Task CorruptPersistedBytesAreNotTreatedAsAMissingLedger()
    {
        var fixture = new PersistenceFixture();
        fixture.Files.Bytes = Utf8("{}");
        await Assert.ThrowsAsync<InstallerProtocolException>(() => fixture.Persistence.LoadAsync(CancellationToken.None));
        Assert.Equal(0, fixture.ActiveAnchors);
        Assert.Equal(Utf8("{}"), fixture.Files.Bytes);
    }

    [Fact]
    public async Task PreCancelledOperationsAcquireNoAnchorAndInvokeNoNativeFileWork()
    {
        var fixture = new PersistenceFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Persistence.LoadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Persistence.SaveAsync(null, CompleteLedger(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Persistence.DeleteAsync(CompleteLedger(), cancellation.Token));
        Assert.Equal(0, fixture.AnchorAcquisitions);
        Assert.Empty(fixture.Files.Paths);
    }

    private static Task RunPersistenceAsync(PersistenceFixture fixture, string operation) => operation switch
    {
        "load" => fixture.Persistence.LoadAsync(CancellationToken.None),
        "save" => fixture.Persistence.SaveAsync(null, CompleteLedger(), CancellationToken.None),
        "delete" => fixture.Persistence.DeleteAsync(CompleteLedger(), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static InstallerTransactionSnapshot Verified() => InstallerTransactionSnapshot.Create(new(
        InstallerTransactionJournal.CurrentSchema, new string('a', 64), InstallerOperation.Uninstall,
        TargetSid, false, "1.0.0.0", new string('d', 64), InstallerTransactionPhase.Verified, 5));

    private static WindowsInstallerDirectoryLedger CompleteLedger() => new(6,
        Enum.GetValues<InstallerDirectoryRole>().Select(role => new WindowsInstallerOwnedDirectory(role, new(12, (ulong)role + 1))), null);

    private sealed class PersistenceFixture
    {
        internal PersistenceFixture()
        {
            Files = new Files(this);
            Persistence = new(new WindowsInstallerDirectoryCleanupLayout(ProgramFiles, ProgramData), Acquire, Files);
        }

        internal WindowsInstallerDirectoryLedgerPersistence Persistence { get; }
        internal Files Files { get; }
        internal int ActiveAnchors { get; private set; }
        internal int AnchorAcquisitions { get; private set; }

        private IDisposable Acquire()
        {
            ActiveAnchors++;
            AnchorAcquisitions++;
            return new Anchor(this);
        }

        private sealed class Anchor(PersistenceFixture owner) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (!_disposed) { _disposed = true; owner.ActiveAnchors--; }
            }
        }
    }

    private sealed class Files(PersistenceFixture fixture) : IWindowsInstallerDirectoryLedgerFileNative
    {
        internal byte[]? Bytes { get; set; }
        internal bool IgnoreMutation { get; set; }
        internal Exception? Failure { get; set; }
        internal List<string> Paths { get; } = [];

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
        {
            Observe(path, cancellationToken);
            return Task.FromResult(Bytes?.ToArray());
        }

        public Task PublishAsync(string path, byte[]? expected, byte[] desired, CancellationToken cancellationToken)
        {
            Observe(path, cancellationToken);
            Assert.Equal(Bytes, expected);
            if (!IgnoreMutation) { Bytes = desired.ToArray(); }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, byte[] expected, CancellationToken cancellationToken)
        {
            Observe(path, cancellationToken);
            Assert.Equal(Bytes, expected);
            if (!IgnoreMutation) { Bytes = null; }
            return Task.CompletedTask;
        }

        private void Observe(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(1, fixture.ActiveAnchors);
            Paths.Add(path);
            if (Failure is { } failure) { throw failure; }
        }
    }
}
