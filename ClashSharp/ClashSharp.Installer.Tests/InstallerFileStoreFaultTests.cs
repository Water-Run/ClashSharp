using System.Security.Cryptography;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Tests;

/// <summary>
/// The guarded single-document stores (transaction journal and certificate ownership ledger): damaged,
/// stale, locked, raced, cancelled and path-policy edge cases must fail closed and leave state recoverable.
/// </summary>
public sealed class InstallerFileStoreFaultTests
{
    public static TheoryData<string> Kinds() => new("transaction", "ledger");

    public static TheoryData<string, int, int> StepPairs()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string kind in new[] { "transaction", "ledger" })
        {
            using InstallerScratchDirectory scratch = new();
            using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
            for (int from = 0; from < harness.Chain.Length; from++)
            {
                for (int to = 0; to < harness.Chain.Length; to++)
                {
                    data.Add(kind, from, to);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StepPairs))]
    public async Task OnlyAnExactReplayOrTheImmediateSuccessorMayReplaceTheCurrentDocument(string kind, int from, int to)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string? hash = await WriteChainAsync(harness, from);
        byte[] before = await File.ReadAllBytesAsync(harness.DocumentPath);

        if (harness.IsLegalStep(from, to))
        {
            string after = await harness.SaveAsync(to, hash, CancellationToken.None);
            Assert.Equal(after, await harness.LoadHashAsync(CancellationToken.None));
            Assert.Equal(harness.Chain[to], await File.ReadAllBytesAsync(harness.DocumentPath));
            Assert.Equal(to == from, after == hash);
        }
        else
        {
            InstallerProtocolException conflict = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                harness.SaveAsync(to, hash, CancellationToken.None));
            Assert.True(
                conflict.DiagnosticCode.EndsWith(".write_conflict", StringComparison.Ordinal)
                    || conflict.DiagnosticCode.EndsWith(".phase_transition_invalid", StringComparison.Ordinal),
                conflict.DiagnosticCode);
            Assert.Equal(before, await File.ReadAllBytesAsync(harness.DocumentPath));
        }

        AssertNoTemporaryFiles(harness);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CreationRequiresNoExpectedHashAndFirstGeneration(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);

        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            harness.SaveAsync(0, InstallerTestData.OtherHash, CancellationToken.None));
        Assert.False(File.Exists(harness.DocumentPath));
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            harness.SaveAsync(1, null, CancellationToken.None));
        Assert.False(File.Exists(harness.DocumentPath));
        Assert.Null(await harness.LoadHashAsync(CancellationToken.None));
        Assert.Empty(harness.RootFiles());

        string hash = await harness.SaveAsync(0, null, CancellationToken.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(harness.Chain[0])), hash);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ATruncatedDocumentIsRefusedAtEveryOffsetAndNeverOverwrittenOrTrusted(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        byte[] document = harness.Chain[0];

        for (int length = 0; length < document.Length; length++)
        {
            await File.WriteAllBytesAsync(harness.DocumentPath, document[..length]);

            await Assert.ThrowsAsync<InstallerProtocolException>(() => harness.LoadAsyncOrThrow());
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                harness.SaveAsync(1, InstallerTestData.Hash, CancellationToken.None));
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                harness.ClearAsync(InstallerTestData.Hash, CancellationToken.None));

            // Recovery never silently repairs: the damaged bytes stay exactly as found.
            Assert.Equal(document[..length], await File.ReadAllBytesAsync(harness.DocumentPath));
            AssertNoTemporaryFiles(harness);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task BitFlippedDocumentsAreRefusedOrLoadedExactlyAsWrittenNeverModified(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        byte[] document = harness.Chain[0];
        Random random = new(20261109);
        for (int attempt = 0; attempt < 400; attempt++)
        {
            byte[] damaged = (byte[])document.Clone();
            int bit = random.Next(damaged.Length * 8);
            damaged[bit / 8] ^= (byte)(1 << (bit % 8));
            await File.WriteAllBytesAsync(harness.DocumentPath, damaged);

            try
            {
                string? hash = await harness.LoadHashAsync(CancellationToken.None);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(damaged)), hash);
            }
            catch (InstallerProtocolException)
            {
                // Refused with the declared exception.
            }

            Assert.Equal(damaged, await File.ReadAllBytesAsync(harness.DocumentPath));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EmptyOversizedAndNonDocumentFilesAreRefusedBeforeAnyParsing(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        foreach (byte[] content in new[]
        {
            [],
            new byte[1],
            new byte[4097],
            new byte[1024 * 1024],
            "{\"schema\":2}"u8.ToArray(),
            "null"u8.ToArray(),
            [0xEF, 0xBB, 0xBF],
        })
        {
            await File.WriteAllBytesAsync(harness.DocumentPath, content);

            InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(() => harness.LoadAsyncOrThrow());

            Assert.StartsWith("installer.", refusal.DiagnosticCode, StringComparison.Ordinal);
            Assert.Equal(content, await File.ReadAllBytesAsync(harness.DocumentPath));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AbandonedTemporaryFilesAreNeverTrustedNeverDeletedAndNeverBlockProgress(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string[] stale =
        [
            Path.Combine(scratch.Path, $".{harness.FileName}.{Guid.NewGuid():N}.tmp"),
            Path.Combine(scratch.Path, $".{harness.FileName}.{Guid.NewGuid():N}.tmp"),
            Path.Combine(scratch.Path, $".{harness.FileName}.00000000000000000000000000000000.tmp"),
        ];
        await File.WriteAllBytesAsync(stale[0], harness.Chain[^1]);
        await File.WriteAllBytesAsync(stale[1], [0x00, 0xFF]);
        await File.WriteAllBytesAsync(stale[2], []);

        // A crashed writer's higher-generation temp file is not state.
        Assert.Null(await harness.LoadHashAsync(CancellationToken.None));
        string hash = await WriteChainAsync(harness, 0) ?? throw new InvalidOperationException();
        hash = await harness.SaveAsync(1, hash, CancellationToken.None);

        Assert.Equal(hash, await harness.LoadHashAsync(CancellationToken.None));
        Assert.All(stale, path => Assert.True(File.Exists(path), "A foreign temporary file was deleted."));
        Assert.Equal(harness.Chain[^1], await File.ReadAllBytesAsync(stale[0]));
        Assert.Equal(stale.Length + 1, harness.RootFiles().Length);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ConcurrentWritersOnOneStoreSerializeAndExactlyOneAdvancesTheDocument(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string? hash = await WriteChainAsync(harness, 0);

        Task<string>[] racers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => harness.SaveAsync(1, hash, CancellationToken.None)))
            .ToArray();
        int succeeded = 0;
        int conflicted = 0;
        foreach (Task<string> racer in racers)
        {
            try
            {
                await racer;
                succeeded++;
            }
            catch (InstallerProtocolException exception)
            {
                Assert.EndsWith(".write_conflict", exception.DiagnosticCode, StringComparison.Ordinal);
                conflicted++;
            }
        }

        // The gate serializes each read-compare-replace: the first advances, replays of the winner
        // carry the stale hash and therefore lose.
        Assert.Equal(1, succeeded);
        Assert.Equal(15, conflicted);
        Assert.Equal(harness.Chain[1], await File.ReadAllBytesAsync(harness.DocumentPath));
        AssertNoTemporaryFiles(harness);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TwoInstallersObservingTheSameStateCannotBothAdvanceIt(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness first = FileStoreHarness.Create(kind, scratch.Path);
        using FileStoreHarness second = first.Clone();
        string? hash = await WriteChainAsync(first, 0);
        Assert.Equal(hash, await second.LoadHashAsync(CancellationToken.None));

        string advanced = await first.SaveAsync(1, hash, CancellationToken.None);
        InstallerProtocolException stale = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            second.SaveAsync(1, hash, CancellationToken.None));

        Assert.EndsWith(".write_conflict", stale.DiagnosticCode, StringComparison.Ordinal);
        Assert.Equal(advanced, await second.LoadHashAsync(CancellationToken.None));
        // The loser must re-read; with the fresh hash its replay of the same step is idempotent.
        Assert.Equal(advanced, await second.SaveAsync(1, advanced, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CancellationLeavesNoTemporaryFileAndNoPartialDocument(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using CancellationTokenSource cancellation = new();
        ScriptedRootGuard guard = new()
        {
            Behavior = (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path, guard);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.SaveAsync(0, null, cancellation.Token));

        Assert.Empty(harness.RootFiles());
        guard.Behavior = null;
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(harness.Chain[0])),
            await harness.SaveAsync(0, null, CancellationToken.None));

        // Cancelling an advance over an existing document keeps the previous generation intact.
        string current = (await harness.LoadHashAsync(CancellationToken.None))!;
        using CancellationTokenSource second = new();
        guard.Behavior = (_, _) =>
        {
            second.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SaveAsync(1, current, second.Token));
        Assert.Equal(harness.Chain[0], await File.ReadAllBytesAsync(harness.DocumentPath));
        AssertNoTemporaryFiles(harness);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task APreCancelledTokenIsHonouredByEveryOperationWithoutTouchingTheRoot(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string? hash = await WriteChainAsync(harness, harness.TerminalIndex);
        string[] before = harness.RootFiles();
        int guardCalls = harness.Guard.Roots.Count;
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.LoadHashAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.SaveAsync(harness.TerminalIndex, hash, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ClearAsync(hash!, cancellation.Token));

        Assert.Equal(before, harness.RootFiles());
        Assert.Equal(guardCalls, harness.Guard.Roots.Count);
        Assert.Equal(harness.Chain[^1], await File.ReadAllBytesAsync(harness.DocumentPath));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AFailingRootGuardStopsEveryOperationBeforeTheFilesystemIsRead(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        ScriptedRootGuard guard = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path, guard);
        string? hash = await WriteChainAsync(harness, harness.TerminalIndex);
        byte[] before = await File.ReadAllBytesAsync(harness.DocumentPath);
        foreach (Exception failure in new Exception[]
        {
            new InstallerProtocolException("installer.test.root_unprotected"),
            new UnauthorizedAccessException("secret-acl-detail"),
            new IOException("secret-io-detail"),
        })
        {
            guard.Behavior = (_, _) => Task.FromException(failure);

            Assert.Same(failure, await Record.ExceptionAsync(() => harness.LoadHashAsync(CancellationToken.None)));
            Assert.Same(failure, await Record.ExceptionAsync(() => harness.SaveAsync(harness.TerminalIndex, hash, CancellationToken.None)));
            Assert.Same(failure, await Record.ExceptionAsync(() => harness.ClearAsync(hash!, CancellationToken.None)));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(harness.DocumentPath));
        AssertNoTemporaryFiles(harness);
        guard.Behavior = null;
        await harness.ClearAsync(hash!, CancellationToken.None);
        Assert.Null(await harness.LoadHashAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AReparsePointAtTheDocumentPathIsRejectedForEveryOperation(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string target = scratch.Combine("outside.json");
        await File.WriteAllBytesAsync(target, harness.Chain[^1]);
        try
        {
            File.CreateSymbolicLink(harness.DocumentPath, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating a link needs a privilege some developer accounts lack; nothing to assert then.
            return;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(harness.Chain[^1]));
        foreach (Func<Task> operation in new Func<Task>[]
        {
            () => harness.LoadAsyncOrThrow(),
            () => harness.SaveAsync(harness.TerminalIndex, hash, CancellationToken.None),
            () => harness.ClearAsync(hash, CancellationToken.None),
        })
        {
            InstallerProtocolException refusal = await Assert.ThrowsAsync<InstallerProtocolException>(operation);
            Assert.EndsWith(".reparse_point_rejected", refusal.DiagnosticCode, StringComparison.Ordinal);
        }

        Assert.Equal(harness.Chain[^1], await File.ReadAllBytesAsync(target));
        Assert.NotEqual(0, (int)(File.GetAttributes(harness.DocumentPath) & FileAttributes.ReparsePoint));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ALockedOrReadOnlyDocumentFailsTheWriteCleanlyAndStaysIntact(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        string? hash = await WriteChainAsync(harness, 0);

        using (FileStream held = new(harness.DocumentPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => harness.LoadHashAsync(CancellationToken.None));
            await Assert.ThrowsAnyAsync<IOException>(() => harness.SaveAsync(1, hash, CancellationToken.None));
        }

        Assert.Equal(harness.Chain[0], await File.ReadAllBytesAsync(harness.DocumentPath));
        AssertNoTemporaryFiles(harness);

        File.SetAttributes(harness.DocumentPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => harness.SaveAsync(1, hash, CancellationToken.None));
            Assert.Equal(harness.Chain[0], await File.ReadAllBytesAsync(harness.DocumentPath));
            AssertNoTemporaryFiles(harness);
        }
        finally
        {
            File.SetAttributes(harness.DocumentPath, FileAttributes.Normal);
        }

        Assert.NotEqual(hash, await harness.SaveAsync(1, hash, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ClearIsRefusedUnlessTheTerminalDocumentMatchesExactlyAndNeverTouchesNeighbours(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        using FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            harness.ClearAsync(InstallerTestData.Hash, CancellationToken.None));

        for (int index = 0; index < harness.TerminalIndex; index++)
        {
            using InstallerScratchDirectory inner = new();
            using FileStoreHarness nonTerminal = FileStoreHarness.Create(kind, inner.Path);
            string? hash = await WriteChainAsync(nonTerminal, index);
            await Assert.ThrowsAsync<InstallerProtocolException>(() => nonTerminal.ClearAsync(hash!, CancellationToken.None));
            Assert.Equal(nonTerminal.Chain[index], await File.ReadAllBytesAsync(nonTerminal.DocumentPath));
        }

        string terminalHash = (await WriteChainAsync(harness, harness.TerminalIndex))!;
        string neighbour = scratch.Combine("neighbour.txt");
        await File.WriteAllTextAsync(neighbour, "keep");
        await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            harness.ClearAsync(InstallerTestData.OtherHash, CancellationToken.None));
        Assert.True(File.Exists(harness.DocumentPath));

        await harness.ClearAsync(terminalHash, CancellationToken.None);
        Assert.False(File.Exists(harness.DocumentPath));
        Assert.True(File.Exists(neighbour));
        await Assert.ThrowsAsync<InstallerProtocolException>(() => harness.ClearAsync(terminalHash, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ADisposedStoreRefusesEveryOperationAndDisposeIsIdempotent(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        FileStoreHarness harness = FileStoreHarness.Create(kind, scratch.Path);
        harness.Dispose();
        harness.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.LoadHashAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.SaveAsync(0, null, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.ClearAsync(InstallerTestData.Hash, CancellationToken.None));
        Assert.Empty(harness.RootFiles());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task RootPathsAreNormalisedExactlyOnceAndNonAsciiSpacedAndLongRootsWork(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        string[] roots =
        [
            scratch.Combine("with space", "\u76ee\u5f55 \u00e9\u00e0", "sub"),
            scratch.Combine(new string('a', 90), new string('b', 90), new string('c', 90), "end"),
            scratch.Combine("\u05e9\u05dc\u05d5\u05dd \u0645\u0631\u062d\u0628\u0627"),
        ];
        foreach (string root in roots)
        {
            Directory.CreateDirectory(root);
            ScriptedRootGuard guard = new();
            using FileStoreHarness harness = FileStoreHarness.Create(kind, root, guard);

            string hash = await harness.SaveAsync(0, null, CancellationToken.None);

            Assert.Equal(hash, await harness.LoadHashAsync(CancellationToken.None));
            Assert.All(guard.Roots, observed => Assert.Equal(root, observed));
            Assert.Equal(harness.Chain[0], await File.ReadAllBytesAsync(harness.DocumentPath));
        }

        Directory.CreateDirectory(scratch.Combine("a", "b"));
        string messy = scratch.Path + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + "b"
            + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "b" + Path.DirectorySeparatorChar
            + Path.DirectorySeparatorChar;
        ScriptedRootGuard messyGuard = new();
        using FileStoreHarness normalised = FileStoreHarness.Create(kind, messy, messyGuard);
        await normalised.SaveAsync(0, null, CancellationToken.None);

        Assert.All(messyGuard.Roots, observed => Assert.Equal(scratch.Combine("a", "b"), observed));
        Assert.True(File.Exists(scratch.Combine("a", "b", normalised.FileName)));
    }

    [Theory]
    [InlineData("transaction", "")]
    [InlineData("transaction", "   ")]
    [InlineData("transaction", "relative")]
    [InlineData("transaction", "relative\\deeper")]
    [InlineData("transaction", "\\rooted-without-drive")]
    [InlineData("transaction", "C:drive-relative")]
    [InlineData("ledger", "")]
    [InlineData("ledger", "   ")]
    [InlineData("ledger", "relative")]
    [InlineData("ledger", "..\\..\\escape")]
    [InlineData("ledger", "\\rooted-without-drive")]
    [InlineData("ledger", "C:drive-relative")]
    public void NonAbsoluteRootsAreRejectedBeforeAnyFilesystemAccess(string kind, string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => FileStoreHarness.Create(kind, root));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task NetworkAndDevicePathsAreOnlyEverReachedThroughTheRootGuard(string kind)
    {
        foreach (string root in new[] { @"\\198.51.100.7\share\installer", @"\\?\UNC\198.51.100.7\share\installer", @"\\.\pipe\installer" })
        {
            ScriptedRootGuard guard = new()
            {
                Behavior = (_, _) => Task.FromException(new InstallerProtocolException("installer.test.root_rejected")),
            };
            using FileStoreHarness harness = FileStoreHarness.Create(kind, root, guard);

            await Assert.ThrowsAsync<InstallerProtocolException>(() => harness.LoadHashAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InstallerProtocolException>(() => harness.SaveAsync(0, null, CancellationToken.None));

            // The guard decided on exactly what the store would have used; nothing was resolved first.
            Assert.Equal(2, guard.Roots.Count);
            Assert.All(guard.Roots, observed => Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), observed));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task StateSurvivesReopeningAfterEveryStepAndALostFinalAcknowledgementIsRecoverable(string kind)
    {
        using InstallerScratchDirectory scratch = new();
        string? hash = null;
        int steps;
        using (FileStoreHarness probe = FileStoreHarness.Create(kind, scratch.Path))
        {
            steps = probe.Chain.Length;
        }

        for (int index = 0; index < steps; index++)
        {
            // A new store instance per step models a process restart between any two durable writes.
            using FileStoreHarness restarted = FileStoreHarness.Create(kind, scratch.Path);
            string? observed = await restarted.LoadHashAsync(CancellationToken.None);
            Assert.Equal(hash, observed);
            hash = await restarted.SaveAsync(index, observed, CancellationToken.None);
            // The acknowledgement is "lost": the next process re-saves the same step idempotently.
            using FileStoreHarness replay = restarted.Clone();
            Assert.Equal(hash, await replay.SaveAsync(index, hash, CancellationToken.None));
        }

        using FileStoreHarness finalStore = FileStoreHarness.Create(kind, scratch.Path);
        await finalStore.ClearAsync(hash!, CancellationToken.None);
        Assert.Null(await finalStore.LoadHashAsync(CancellationToken.None));
        Assert.Empty(finalStore.RootFiles());
    }

    private static async Task<string?> WriteChainAsync(FileStoreHarness harness, int lastIndex)
    {
        string? hash = null;
        for (int index = 0; index <= lastIndex; index++)
        {
            hash = await harness.SaveAsync(index, hash, CancellationToken.None);
        }

        return hash;
    }

    private static void AssertNoTemporaryFiles(FileStoreHarness harness) =>
        Assert.DoesNotContain(
            harness.RootFiles(),
            static name => name.EndsWith(".tmp", StringComparison.Ordinal));
}

internal static class FileStoreHarnessExtensions
{
    /// <summary>Loads and fails with whatever the store raises; a successful absent document is itself an error.</summary>
    internal static async Task LoadAsyncOrThrow(this FileStoreHarness harness)
    {
        string? hash = await harness.LoadHashAsync(CancellationToken.None);
        throw new InvalidOperationException($"The store unexpectedly accepted the document ({hash}).");
    }
}
