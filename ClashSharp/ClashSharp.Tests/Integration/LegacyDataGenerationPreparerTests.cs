using System.Security.Cryptography;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Integration;

public sealed class LegacyDataGenerationPreparerTests
{
    [Fact]
    public async Task MigratesSettingsProfilesAndCommittedWalRecords_WithoutPublishingOrChangingLegacyData()
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        Dictionary<string, string> documents = new(StringComparer.Ordinal)
        {
            ["ProfileCatalog.json"] = "{\"Profiles\":[],\"Links\":[]}",
            ["Triggers.json"] = "{\"Tasks\":[]}",
            ["Triggers.json.migration-intent"] = "retained trigger migration identity",
            [Path.Combine("mihomo", "proxy-selections.json")] = "{\"version\":1,\"profiles\":{\"example\":{\"GLOBAL\":\"Node B\"}}}",
            [Path.Combine("mihomo", "profiles", "example", "config.yaml")] = "proxies: []\nrules: [MATCH,DIRECT]",
            [Path.Combine("mihomo", "history", "example", "revision.yaml")] = "rules: [MATCH,REJECT]",
        };
        foreach ((string path, string contents) in documents)
        {
            string file = Path.Combine(directory.RootPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, contents);
        }
        await File.WriteAllTextAsync(Path.Combine(directory.RootPath, "mihomo", "config.yaml"), "old generated runtime with credentials");
        await File.WriteAllTextAsync(Path.Combine(directory.RootPath, "unrelated-private-file"), "retain only at source");
        using SqliteConnection logs = CreateWalDatabase(directory, "ClashSharpLogs.sqlite3", 37);
        using SqliteConnection triggers = CreateWalDatabase(directory, "Triggers.db", 53);
        using SqliteConnection triggerBackup = CreateWalDatabase(directory, "Triggers.db.backup", 19);
        Dictionary<string, string> before = SnapshotFiles(directory.RootPath);
        MutationAdmissionBarrier admission = new();
        Source source = new();
        LegacyDataGenerationPreparer preparer = new(directory.RootPath, admission, source, SettingsRegistry.Default);
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        DataGenerationDescriptor candidate = await preparer.PrepareAdmittedAsync(lease, CancellationToken.None);

        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Equal(1, candidate.GenerationNumber);
        Assert.Equal(1, source.Reads);
        foreach ((string path, string contents) in documents)
        {
            Assert.Equal(contents, await File.ReadAllTextAsync(Path.Combine(candidate.RootPath, path)));
        }
        Assert.Equal(37, ReadCount(candidate, "ClashSharpLogs.sqlite3"));
        Assert.Equal(53, ReadCount(candidate, "Triggers.db"));
        Assert.Equal(19, ReadCount(candidate, "Triggers.db.backup"));
        foreach ((string path, string hash) in before)
        {
            Assert.Equal(hash, HashFile(path));
        }
        Assert.False(File.Exists(Path.Combine(candidate.RootPath, "mihomo", "config.yaml")));
        Assert.False(File.Exists(Path.Combine(candidate.RootPath, "unrelated-private-file")));
        SettingsPersistenceResult settings = await new JsonSettingsRepository(candidate, SettingsRegistry.Default).OpenAsync(CancellationToken.None);
        Assert.Equal(10808, settings.Envelope!.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.NotEmpty(settings.Envelope.PendingApplications);
        Assert.DoesNotContain("controller-secret-test", await File.ReadAllTextAsync(new JsonSettingsRepository(candidate, SettingsRegistry.Default).PrimaryPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingGeneration_IsNeverReplacedOrRemigrated()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot current = await directory.PromoteFirstAsync();
        MutationAdmissionBarrier admission = new();
        Source source = new();
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LegacyDataGenerationPreparer(directory.RootPath, admission, source, SettingsRegistry.Default)
            .PrepareAdmittedAsync(lease, CancellationToken.None));
        Assert.Equal(0, source.Reads);
        Assert.Single(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.Equal(current.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task CorruptDatabase_DoesNotPublishOrInitializePreferences_AndKeepsSourceBytes()
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string database = Path.Combine(directory.RootPath, "ClashSharpLogs.sqlite3");
        byte[] corrupt = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(database, corrupt);
        MutationAdmissionBarrier admission = new();
        Source source = new();
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => new LegacyDataGenerationPreparer(directory.RootPath, admission, source, SettingsRegistry.Default)
            .PrepareAdmittedAsync(lease, CancellationToken.None));

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(database));
        Assert.Equal(0, source.Reads);
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Single(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
    }

    [Fact]
    public async Task FailedPreferenceRead_LeavesOriginalFilesAndUnpublishedCandidateForDiagnosis()
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string catalog = Path.Combine(directory.RootPath, "ProfileCatalog.json");
        await File.WriteAllTextAsync(catalog, "{\"Profiles\":[]}");
        MutationAdmissionBarrier admission = new();
        IOException expected = new("Legacy settings unavailable.");
        Source source = new() { Failure = expected };
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => new LegacyDataGenerationPreparer(directory.RootPath, admission, source, SettingsRegistry.Default)
            .PrepareAdmittedAsync(lease, CancellationToken.None)));

        Assert.Equal("{\"Profiles\":[]}", await File.ReadAllTextAsync(catalog));
        string retained = Assert.Single(Directory.GetDirectories(directory.Policy.GenerationsRootPath));
        Assert.Equal(await File.ReadAllTextAsync(catalog), await File.ReadAllTextAsync(Path.Combine(retained, "ProfileCatalog.json")));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelBeforePreparation_DoesNotAllocateStorageOrReadPreferences()
    {
        await using DataGenerationTestDirectory directory = new();
        MutationAdmissionBarrier admission = new();
        Source source = new();
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LegacyDataGenerationPreparer(directory.RootPath, admission, source, SettingsRegistry.Default)
            .PrepareAdmittedAsync(lease, new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("ProfileCatalog.json")]
    [InlineData("Triggers.db")]
    [InlineData("mihomo/proxy-selections.json")]
    public async Task DirectoryInPlaceOfAFile_FailsWithoutSilentlyDroppingData(string relativePath)
    {
        await using DataGenerationTestDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.RootPath, relativePath));
        MutationAdmissionBarrier admission = new();
        Source source = new();
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LegacyDataGenerationPreparer(directory.RootPath, admission, source, SettingsRegistry.Default)
            .PrepareAdmittedAsync(lease, CancellationToken.None));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    private static SqliteConnection CreateWalDatabase(DataGenerationTestDirectory directory, string name, int count)
    {
        string path = Path.Combine(directory.RootPath, name);
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Records (Value INTEGER NOT NULL); INSERT INTO Records VALUES ($count);";
        command.Parameters.AddWithValue("$count", count);
        command.ExecuteNonQuery();
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        return connection;
    }

    private static long ReadCount(DataGenerationDescriptor descriptor, string database)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(descriptor.RootPath, database),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Records;";
        return (long)command.ExecuteScalar()!;
    }

    private static Dictionary<string, string> SnapshotFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => path, HashFile, StringComparer.Ordinal);

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class Source : ILegacySettingsSource
    {
        public int Reads { get; private set; }
        public Exception? Failure { get; init; }
        public Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            ++Reads;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(new LegacySettingsSnapshot(SettingsRegistry.Default, new Dictionary<string, object?>
            {
                ["MixedPort"] = 10808,
                ["ControllerSecret"] = "controller-secret-test",
            }));
        }
    }
}
