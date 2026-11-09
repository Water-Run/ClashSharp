extern alias RecoveryWatchdog;

using System.Diagnostics;
using System.Globalization;
using Invocation = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogInvocation;
using Lease = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLease;
using LeaseStore = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogLeaseFileStore;
using Paths = RecoveryWatchdog::ClashSharp.Recovery.RecoveryWatchdogPaths;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>Contract of the watchdog command line, lease record and lease file.</summary>
public sealed class WatchdogProtocolContractTests
{
    private static readonly Guid Nonce = new("0123456789abcdef0123456789abcdef");

    private static string[] ValidArguments(long ticks = 638_000_000_000_000_001L, int pid = 4242) =>
    [
        "--nonce", Nonce.ToString("N"),
        "--parent-pid", pid.ToString(CultureInfo.InvariantCulture),
        "--parent-start-utc-ticks", ticks.ToString(CultureInfo.InvariantCulture),
    ];

    [Fact]
    public void Parse_AcceptsTheThreeOptionPairsInEveryOrder()
    {
        string[] arguments = ValidArguments();
        Invocation expected = new(Nonce, 4242, 638_000_000_000_000_001L);
        int[][] permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

        foreach (int[] order in permutations)
        {
            string[] reordered = [.. order.SelectMany(pair => new[] { arguments[pair * 2], arguments[(pair * 2) + 1] })];

            Assert.Equal(expected, Invocation.Parse(reordered));
        }
    }

    [Fact]
    public void AddTo_ThenParse_RoundTripsForGeneratedInvocations()
    {
        Random random = new(20261109);
        for (int round = 0; round < 500; round++)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            Guid nonce = new(bytes);
            if (nonce == Guid.Empty)
            {
                continue;
            }

            Invocation original = new(nonce, random.Next(1, int.MaxValue), random.NextInt64(1, long.MaxValue));
            ProcessStartInfo startInfo = new("ignored.exe");

            original.AddTo(startInfo);
            Invocation parsed = Invocation.Parse([.. startInfo.ArgumentList]);

            Assert.Equal(original, parsed);
            Assert.Equal(original.ToLease(), parsed.ToLease());
            Assert.Equal(6, startInfo.ArgumentList.Count);
        }
    }

    [Fact]
    public void AddTo_RejectsNullStartInfo()
    {
        Assert.Throws<ArgumentNullException>(() => new Invocation(Nonce, 1, 1).AddTo(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void Parse_RejectsEveryWrongArgumentCount(int count)
    {
        string[] arguments = [.. ValidArguments(), "--extra", "x"];

        Assert.Throws<ArgumentException>(() => Invocation.Parse([.. arguments.Take(count)]));
    }

    [Fact]
    public void Parse_RejectsDuplicateUnknownAndMisnamedOptions()
    {
        string[] valid = ValidArguments();
        string[][] hostile =
        [
            ["--nonce", valid[1], "--nonce", valid[1], "--parent-pid", "1"],
            ["--nonce", valid[1], "--parent-pid", "1", "--parent-pid", "2"],
            ["--nonce", valid[1], "--parent-pid", "1", "--unknown", "5"],
            ["--NONCE", valid[1], "--parent-pid", "1", "--parent-start-utc-ticks", "1"],
            ["nonce", valid[1], "--parent-pid", "1", "--parent-start-utc-ticks", "1"],
            ["--nonce", valid[1], "--parent-pid", "1", "--parent-start-utc-ticks"],
            ["--nonce", "--parent-pid", "1", "--parent-pid", "--parent-start-utc-ticks"],
        ];

        foreach (string[] arguments in hostile)
        {
            Assert.Throws<ArgumentException>(() => Invocation.Parse(arguments));
        }
    }

    /// <summary>Gets nonce spellings that are not the 32-hex "N" form.</summary>
    public static TheoryData<string> BadNonces => new()
    {
        string.Empty,
        " ",
        "00000000000000000000000000000000",
        "0123456789abcdef0123456789abcde",
        "0123456789abcdef0123456789abcdef0",
        "01234567-89ab-cdef-0123-456789abcdef",
        "{01234567-89ab-cdef-0123-456789abcdef}",
        "(01234567-89ab-cdef-0123-456789abcdef)",
        "0123456789abcdeg0123456789abcdef",
        "0x0123456789abcdef0123456789abcdef",
        "０１２３４５６７８９abcdef0123456789abcdef",
    };

    [Theory]
    [MemberData(nameof(BadNonces))]
    public void Parse_RejectsEveryNonCanonicalNonce(string nonce)
    {
        string[] arguments = ValidArguments();
        arguments[1] = nonce;

        Assert.Throws<ArgumentException>(() => Invocation.Parse(arguments));
    }

    /// <summary>Gets numeric spellings that are not a plain positive decimal.</summary>
    public static TheoryData<string> BadNumbers => new()
    {
        string.Empty,
        " ",
        "0",
        "-1",
        "+5",
        "1.5",
        "1e3",
        "0x10",
        " 7",
        "7 ",
        "1,000",
        "٣",
        "99999999999999999999999",
        "2147483648",
    };

    [Theory]
    [MemberData(nameof(BadNumbers))]
    public void Parse_RejectsEveryNonPositiveDecimalProcessId(string number)
    {
        string[] arguments = ValidArguments();
        arguments[3] = number;

        Assert.Throws<ArgumentException>(() => Invocation.Parse(arguments));
    }

    [Theory]
    [MemberData(nameof(BadNumbers))]
    public void Parse_RejectsEveryNonPositiveDecimalStartTime(string number)
    {
        if (number == "2147483648")
        {
            return; // Valid for a 64-bit tick count, covered by the accepted-range test.
        }

        string[] arguments = ValidArguments();
        arguments[5] = number;

        Assert.Throws<ArgumentException>(() => Invocation.Parse(arguments));
    }

    [Fact]
    public void Parse_AcceptsTheFullPositiveRange()
    {
        Invocation parsed = Invocation.Parse(ValidArguments(long.MaxValue, int.MaxValue));

        Assert.Equal(int.MaxValue, parsed.ParentProcessId);
        Assert.Equal(long.MaxValue, parsed.ParentStartTimeUtcTicks);
        Assert.Equal(1, Invocation.Parse(ValidArguments(1, 1)).ParentProcessId);
    }

    [Fact]
    public void Lease_ValidationRejectsEachInvalidField_AndRecordEqualityIsFieldWise()
    {
        Lease valid = new(Lease.CurrentSchemaVersion, Nonce, 10, 20);
        valid.Validate();

        Lease[] invalid =
        [
            valid with { SchemaVersion = 0 },
            valid with { SchemaVersion = Lease.CurrentSchemaVersion + 1 },
            valid with { SchemaVersion = -1 },
            valid with { Nonce = Guid.Empty },
            valid with { ParentProcessId = 0 },
            valid with { ParentProcessId = -4 },
            valid with { ParentStartTimeUtcTicks = 0 },
            valid with { ParentStartTimeUtcTicks = -9 },
        ];
        foreach (Lease lease in invalid)
        {
            Assert.Throws<InvalidDataException>(lease.Validate);
        }

        Assert.Equal(valid, new Lease(Lease.CurrentSchemaVersion, Nonce, 10, 20));
        Assert.NotEqual(valid, valid with { ParentProcessId = 11 });
    }

    [Fact]
    public void LeaseStore_MissingFileIsAbsence_AndWriteReadRoundTripsGeneratedLeases()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "nested", "deeper", "lease.json");
        LeaseStore store = new(path);
        Assert.Null(store.Read());
        Random random = new(20261109);

        for (int round = 0; round < 100; round++)
        {
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            Lease lease = new(
                Lease.CurrentSchemaVersion,
                new Guid(bytes),
                random.Next(1, int.MaxValue),
                random.NextInt64(1, long.MaxValue));

            store.Write(lease);

            Assert.Equal(lease, store.Read());
            Assert.Equal(
                "lease.json",
                Path.GetFileName(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!))));
        }
    }

    [Fact]
    public void LeaseStore_WriteRejectsInvalidLeasesWithoutTouchingTheExistingFile()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "lease.json");
        LeaseStore store = new(path);
        Lease good = new(Lease.CurrentSchemaVersion, Nonce, 10, 20);
        store.Write(good);
        string before = File.ReadAllText(path);

        Assert.Throws<InvalidDataException>(() => store.Write(good with { Nonce = Guid.Empty }));
        Assert.Throws<ArgumentNullException>(() => store.Write(null!));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(good, store.Read());
    }

    [Fact]
    public void LeaseStore_ATruncatedFileAtEveryByteOffset_NeverYieldsADifferentLease()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "lease.json");
        LeaseStore store = new(path);
        Lease lease = new(Lease.CurrentSchemaVersion, Nonce, 31337, 638_123_456_789_012_345L);
        store.Write(lease);
        byte[] complete = File.ReadAllBytes(path);

        for (int length = 0; length <= complete.Length; length++)
        {
            File.WriteAllBytes(path, complete[..length]);

            Exception? failure = Record.Exception(() => store.Read());

            if (failure is null)
            {
                // A prefix may only be accepted when it still holds the entire record.
                Lease? read = store.Read();
                Assert.Equal(lease, read);
            }
            else
            {
                Assert.IsType<InvalidDataException>(failure);
            }
        }

        File.WriteAllBytes(path, complete);
        Assert.Equal(lease, store.Read());
    }

    /// <summary>Gets hostile lease file contents.</summary>
    public static TheoryData<string> HostileLeaseDocuments => new()
    {
        string.Empty,
        "null",
        "[]",
        "{}",
        "\"text\"",
        "12",
        "{\"SchemaVersion\":1}",
        "{\"SchemaVersion\":\"1\",\"Nonce\":\"0123456789abcdef0123456789abcdef\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"not-a-guid\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"00000000-0000-0000-0000-000000000000\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":0,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":-1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":1.5,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":99999999999,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":2,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":1}",
        "{\"SchemaVersion\":1,\"Nonce\":\"01234567-89ab-cdef-0123-456789abcdef\",\"ParentProcessId\":1,\"ParentStartTimeUtcTicks\":1,",
        "\0\0\0\0",
    };

    [Theory]
    [MemberData(nameof(HostileLeaseDocuments))]
    public void LeaseStore_RejectsEveryHostileDocumentWithTheStableException(string text)
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "lease.json");
        File.WriteAllText(path, text);
        LeaseStore store = new(path);

        Assert.Throws<InvalidDataException>(store.Read);
        // A corrupt lease is never silently deleted: it cannot match any expected lease.
        Assert.Throws<InvalidDataException>(() => store.ClearIfMatches(new Lease(Lease.CurrentSchemaVersion, Nonce, 1, 1)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LeaseStore_ClearIfMatchesIsANoOpWithoutAFile_AndRejectsNull()
    {
        using TempDirectory directory = new();
        LeaseStore store = new(Path.Combine(directory.Path, "none.json"));

        store.ClearIfMatches(new Lease(Lease.CurrentSchemaVersion, Nonce, 1, 1));

        Assert.Throws<ArgumentNullException>(() => store.ClearIfMatches(null!));
        Assert.Throws<ArgumentException>(() => new LeaseStore(" "));
        Assert.Throws<ArgumentException>(() => new LeaseStore(string.Empty));
    }

    [Fact]
    public void LeaseStore_ClearIfMatches_RemovesOnlyAnExactMatch_ForEveryFieldDifference()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "lease.json");
        LeaseStore store = new(path);
        Lease stored = new(Lease.CurrentSchemaVersion, Nonce, 10, 20);
        Lease[] different =
        [
            stored with { Nonce = Guid.NewGuid() },
            stored with { ParentProcessId = 11 },
            stored with { ParentStartTimeUtcTicks = 21 },
        ];

        foreach (Lease other in different)
        {
            store.Write(stored);
            store.ClearIfMatches(other);
            Assert.Equal(stored, store.Read());
        }

        store.ClearIfMatches(stored);
        Assert.Null(store.Read());
    }

    [Fact]
    public void Paths_ResolveUnderTheUserProfileWithoutCreatingAnything()
    {
        string local = Paths.ResolveLocalDataDirectory();
        string installerLock = Paths.ResolveInstallerMutationLockPath();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathFullyQualified(local));
        Assert.StartsWith(localAppData, local, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(localAppData, installerLock, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Paths.LeaseFileName, Path.GetFileName(Path.Combine(local, Paths.LeaseFileName)));
        Assert.NotEqual(Paths.LeaseFileName, Paths.LockFileName);
        Assert.NotEqual(Paths.ProxyJournalFileName, Paths.LeaseFileName);
        Assert.All(
            [Paths.LeaseFileName, Paths.LockFileName, Paths.ProxyJournalFileName],
            name => Assert.Equal(Path.GetFileName(name), name));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ClashSharp.Tests.Watchdog",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
