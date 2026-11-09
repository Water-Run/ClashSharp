using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Fault injection at every effect boundary of the profile import transactions. Invariant after any failure:
/// the installed source, the catalog rows and the retained history describe the same, previous generation, and
/// nothing is staged or orphaned on disk.
/// </summary>
public sealed class ProfileCatalogFaultInjectionTests
{
    private const string V1 = "proxies: []\nrules:\n  - MATCH,DIRECT # v1\n";
    private const string V2 = "proxies: []\nrules:\n  - MATCH,REJECT # v2\n";

    public enum Fault
    {
        /// <summary>The staging directory cannot be created.</summary>
        StagingBlocked,
        /// <summary>The core refuses the candidate while importing it.</summary>
        CoreImportThrows,
        /// <summary>The durable catalog write fails after the source was installed.</summary>
        CatalogSaveFails,
        /// <summary>History promotion fails after the source was installed.</summary>
        HistoryPromotionBlocked,
        /// <summary>The live runtime reports that the active candidate was not applied.</summary>
        RuntimeNotApplied,
    }

    public static TheoryData<Fault, bool, bool> Matrix()
    {
        TheoryData<Fault, bool, bool> data = [];
        foreach (Fault fault in Enum.GetValues<Fault>())
        {
            foreach (bool hasPrevious in new[] { false, true })
            {
                foreach (bool active in new[] { false, true })
                {
                    // A profile that the runtime reports as active always has an installed source to restore.
                    if (active && !hasPrevious)
                    {
                        continue;
                    }

                    data.Add(fault, hasPrevious, active);
                }
            }
        }

        return data;
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ProfileHistoryDirectory(CatalogHarness harness, string profileId) =>
        Path.Combine(harness.HistoryRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileId))));

    private static void SabotageCatalogFile(CatalogHarness harness)
    {
        File.Delete(harness.CatalogPath);
        Directory.CreateDirectory(harness.CatalogPath);
        File.WriteAllText(Path.Combine(harness.CatalogPath, "block"), "x");
    }

    private static void HealCatalogFile(CatalogHarness harness)
    {
        if (Directory.Exists(harness.CatalogPath))
        {
            Directory.Delete(harness.CatalogPath, recursive: true);
        }
    }

    private static void AssertConsistent(CatalogHarness harness, string profileId, string? expectedSource, string context)
    {
        Assert.True(expectedSource == harness.Sources.Get(profileId), $"{context}: installed source");
        IReadOnlyList<ProfileHistoryEntry> entries = harness.Service.GetProfileHistory(profileId);
        string[] files = [.. harness.HistoryFiles().Select(static path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal)];
        Assert.True(
            entries.Select(static entry => entry.VersionId).Order(StringComparer.Ordinal).SequenceEqual(files),
            $"{context}: history entries {entries.Count} vs files {files.Length}");
        foreach (ProfileHistoryEntry entry in entries)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(ProfileHistoryDirectory(harness, profileId), entry.VersionId + ".yaml"));
            Assert.Equal(entry.ContentSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        if (expectedSource is null)
        {
            Assert.Empty(entries);
            Assert.DoesNotContain(harness.Service.GetProfiles(), profile => profile.Id == profileId);
        }
        else
        {
            Assert.Equal(Sha(expectedSource), entries[0].ContentSha256);
            Assert.Contains(harness.Service.GetProfiles(), profile => profile.Id == profileId);
        }

        Assert.Empty(harness.PendingHistoryFiles());
    }

    private static void Arm(CatalogHarness harness, Fault fault, string profileId)
    {
        switch (fault)
        {
            case Fault.StagingBlocked:
                string pending = Path.Combine(harness.HistoryRoot, ".pending");
                if (Directory.Exists(pending))
                {
                    Directory.Delete(pending, recursive: true);
                }

                Directory.CreateDirectory(harness.HistoryRoot);
                File.WriteAllText(pending, "x");
                break;
            case Fault.CoreImportThrows:
                harness.Core.BeforeImport = (_, _) => throw new IOException("injected core failure");
                harness.Runtime.BeforeImportAndApply = () => throw new IOException("injected runtime failure");
                break;
            case Fault.CatalogSaveFails:
                harness.Sources.BeforeNextSet = () => SabotageCatalogFile(harness);
                break;
            case Fault.HistoryPromotionBlocked:
                harness.Sources.BeforeNextSet = () =>
                {
                    string directory = ProfileHistoryDirectory(harness, profileId);
                    if (Directory.Exists(directory))
                    {
                        Directory.Move(directory, directory + ".moved");
                    }

                    File.WriteAllText(directory, "x");
                };
                break;
            case Fault.RuntimeNotApplied:
                harness.Runtime.ApplyResult = false;
                break;
            default:
                throw new NotSupportedException("Every fault must define how it is armed: " + fault);
        }
    }

    private static void Heal(CatalogHarness harness, Fault fault, string profileId)
    {
        switch (fault)
        {
            case Fault.StagingBlocked:
                File.Delete(Path.Combine(harness.HistoryRoot, ".pending"));
                break;
            case Fault.CoreImportThrows:
                harness.Core.BeforeImport = null;
                harness.Runtime.BeforeImportAndApply = null;
                break;
            case Fault.CatalogSaveFails:
                HealCatalogFile(harness);
                break;
            case Fault.HistoryPromotionBlocked:
                string directory = ProfileHistoryDirectory(harness, profileId);
                if (File.Exists(directory))
                {
                    File.Delete(directory);
                }

                if (Directory.Exists(directory + ".moved"))
                {
                    Directory.Move(directory + ".moved", directory);
                }

                break;
            case Fault.RuntimeNotApplied:
                harness.Runtime.ApplyResult = true;
                break;
            default:
                throw new NotSupportedException("Every fault must define how it heals: " + fault);
        }
    }

    // ----------------------------------------------------------------------- subscription import

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task SubscriptionImport_FailureAtEveryBoundaryLeavesThePreviousGenerationIntact(
        Fault fault, bool hasPrevious, bool active)
    {
        await using CatalogHarness harness = new();
        string served = V1;
        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(c, 200, served));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);
        string profileId = "subscription-" + link.Id;
        if (hasPrevious)
        {
            await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);
            Assert.Equal(V1, harness.Sources.Get(profileId));
        }

        served = V2;
        if (active)
        {
            harness.Runtime.ObservedActiveProfileId = profileId;
        }

        Arm(harness, fault, profileId);
        ProfileSubscriptionLink current = harness.Service.GetSubscriptionLinks().Single();
        string context = $"{fault}/previous={hasPrevious}/active={active}";

        // RuntimeNotApplied is only observable when the live runtime owns the profile; otherwise the import is inert.
        bool expectFailure = fault != Fault.RuntimeNotApplied || active;
        if (expectFailure)
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => harness.Service.ImportSubscriptionLinkAsync(current, limit.Token));
            Assert.False(failure is OperationCanceledException, context + ": " + failure.GetType().Name);
            Heal(harness, fault, profileId);
            AssertConsistent(harness, profileId, hasPrevious ? V1 : null, context);
        }
        else
        {
            await harness.Service.ImportSubscriptionLinkAsync(current, limit.Token);
            Heal(harness, fault, profileId);
            AssertConsistent(harness, profileId, V2, context);
            return;
        }

        // Liveness: after the fault is healed the very same link imports normally and generations stay consistent.
        ProfileSubscriptionLink retry = harness.Service.GetSubscriptionLinks().Single();
        await harness.Service.ImportSubscriptionLinkAsync(retry, limit.Token);
        AssertConsistent(harness, profileId, V2, context + "/retry");
        Assert.Equal(hasPrevious ? 2 : 1, harness.Service.GetProfileHistory(profileId).Count);
    }

    // ----------------------------------------------------------------------- local import

    [Theory]
    [InlineData(Fault.StagingBlocked)]
    [InlineData(Fault.CoreImportThrows)]
    [InlineData(Fault.CatalogSaveFails)]
    public async Task LocalImport_FailureLeavesNoProfileSourceHistoryOrStagedFile(Fault fault)
    {
        await using CatalogHarness harness = new();
        string file = harness.Directory.Combine("input", "my profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, V1);
        Arm(harness, fault, "unused");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Service.ImportLocalProfileAsync(file, CancellationToken.None));
        Heal(harness, fault, "unused");

        Assert.Empty(harness.Sources.Snapshot());
        Assert.Equal(
            [ProfileCatalogIds.BuiltInDirect],
            harness.Service.GetProfiles().Select(static profile => profile.Id).ToArray());
        Assert.Empty(harness.HistoryFiles());

        ProfileImportResult retried = await harness.Service.ImportLocalProfileAsync(file, CancellationToken.None);
        Assert.Equal(V1, harness.Sources.Get(retried.ProfileId));
        Assert.Single(harness.Service.GetProfileHistory(retried.ProfileId));
    }

    [Fact]
    public async Task LocalImport_FileNamesBecomeProfileNamesWithoutLeakingPathsAndEveryImportGetsAFreshIdentity()
    {
        await using CatalogHarness harness = new();
        string[] names =
        [
            "plain.yaml", "with space.yml", "多字节 配置.yaml", "עברית.yaml", "😀.txt", "dots.in.name.yaml", "NOEXT",
            new string('n', 120) + ".yaml", "UPPER.YAML", "trailing space .yaml",
        ];
        HashSet<string> ids = [];
        foreach (string name in names)
        {
            string path = harness.Directory.Combine("in put", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, V1);

            ProfileImportResult result = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
            ProfileImportResult again = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);

            Assert.Equal(Path.GetFileNameWithoutExtension(name), result.ProfileName);
            Assert.StartsWith("local-", result.ProfileId, StringComparison.Ordinal);
            Assert.NotEqual(result.ProfileId, again.ProfileId);
            Assert.True(ids.Add(result.ProfileId) && ids.Add(again.ProfileId));
            ConfigurationProfile row = harness.Service.GetProfiles().Single(profile => profile.Id == result.ProfileId);
            Assert.Equal(name, row.SourceName);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, row.SourceName);
            Assert.Equal(V1, harness.Sources.Get(result.ProfileId));
        }
    }

    [Fact]
    public async Task LocalImport_UnreadableInputsFailBeforeAnyEffect()
    {
        await using CatalogHarness harness = new();
        string missing = harness.Directory.Combine("missing.yaml");
        await Assert.ThrowsAsync<FileNotFoundException>(() => harness.Service.ImportLocalProfileAsync(missing, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.ImportLocalProfileAsync("   ", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.ImportLocalProfileAsync(null!, CancellationToken.None));

        string locked = harness.Directory.Combine("locked.yaml");
        File.WriteAllText(locked, V1);
        await using (FileStream exclusive = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => harness.Service.ImportLocalProfileAsync(locked, CancellationToken.None));
        }

        string directoryAsFile = harness.Directory.Combine("a-directory");
        Directory.CreateDirectory(directoryAsFile);
        await Assert.ThrowsAsync<FileNotFoundException>(() => harness.Service.ImportLocalProfileAsync(directoryAsFile, CancellationToken.None));

        Assert.Equal(0, harness.Core.ImportCalls);
        Assert.Empty(harness.HistoryFiles());
        Assert.Single(harness.Service.GetProfiles());
    }

    [Theory]
    [InlineData(4 * 1024 * 1024, true)]
    [InlineData(4 * 1024 * 1024 + 1, false)]
    public async Task LocalImport_SizeLimitIsExactAtFourMebibytes(int length, bool accepted)
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("size.yaml");
        byte[] bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        File.WriteAllBytes(path, bytes);

        if (accepted)
        {
            ProfileImportResult result = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
            Assert.Equal(length, Encoding.UTF8.GetByteCount(harness.Sources.Get(result.ProfileId)!));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => harness.Service.ImportLocalProfileAsync(path, CancellationToken.None));
            Assert.Equal(0, harness.Core.ImportCalls);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, "a")]
    [InlineData(new byte[] { (byte)'a', 0xEF, 0xBB, 0xBF }, "a﻿")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'a' }, "﻿a")]
    [InlineData(new byte[] { (byte)'a', 0, (byte)'b' }, "a\0b")]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, "😀")]
    public async Task LocalImport_DecodesStrictUtf8AndStripsExactlyOneLeadingBom(byte[] bytes, string expected)
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("bom.yaml");
        File.WriteAllBytes(path, bytes);

        ProfileImportResult result = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);

        Assert.Equal(expected, harness.Sources.Get(result.ProfileId));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC3 })]
    [InlineData(new byte[] { 0xE2, 0x82 })]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]
    public async Task LocalImport_RejectsEveryNonUtf8ByteSequenceBeforeTheCoreSeesIt(byte[] bytes)
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("bad.yaml");
        File.WriteAllBytes(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Service.ImportLocalProfileAsync(path, CancellationToken.None));

        Assert.Equal(0, harness.Core.ImportCalls);
        Assert.Empty(harness.HistoryFiles());
    }

    // ----------------------------------------------------------------------- rollback

    [Theory]
    [InlineData(Fault.CoreImportThrows, false)]
    [InlineData(Fault.CatalogSaveFails, false)]
    [InlineData(Fault.CatalogSaveFails, true)]
    [InlineData(Fault.RuntimeNotApplied, true)]
    public async Task Rollback_FailureKeepsTheCurrentGenerationAndItsHistory(Fault fault, bool active)
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("rb.yaml");
        File.WriteAllText(path, V1);
        ProfileImportResult first = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
        string profileId = first.ProfileId;
        ProfileHistoryEntry v1Entry = harness.Service.GetProfileHistory(profileId).Single();
        // Install a second generation through the agent/source-edit path so there is something to roll back from.
        harness.Sources.Set(profileId, V2);
        if (active)
        {
            harness.Runtime.ObservedActiveProfileId = profileId;
        }

        Arm(harness, fault, profileId);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Service.RollbackProfileAsync(v1Entry, CancellationToken.None));
        Heal(harness, fault, profileId);

        Assert.Equal(V2, harness.Sources.Get(profileId));
        Assert.Single(harness.Service.GetProfileHistory(profileId));
        Assert.Empty(harness.PendingHistoryFiles());

        ProfileImportResult rolledBack = await harness.Service.RollbackProfileAsync(v1Entry, CancellationToken.None);
        Assert.Equal(profileId, rolledBack.ProfileId);
        Assert.Equal(V1, harness.Sources.Get(profileId));
        Assert.Equal(2, harness.Service.GetProfileHistory(profileId).Count);
    }

    [Fact]
    public async Task Rollback_TamperedOrForeignHistoryIsRejectedWithoutEffect()
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("rb.yaml");
        File.WriteAllText(path, V1);
        ProfileImportResult imported = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
        ProfileHistoryEntry entry = harness.Service.GetProfileHistory(imported.ProfileId).Single();
        int importsBefore = harness.Core.ImportCalls;

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RollbackProfileAsync(
            entry with { ContentSha256 = new string('0', 64) }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RollbackProfileAsync(
            entry with { ProfileId = "someone-else" }, CancellationToken.None));

        string file = Assert.Single(harness.HistoryFiles());
        File.WriteAllText(file, V2);
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Service.RollbackProfileAsync(entry, CancellationToken.None));
        File.Delete(file);
        await Assert.ThrowsAnyAsync<IOException>(() => harness.Service.RollbackProfileAsync(entry, CancellationToken.None));

        Assert.Equal(importsBefore, harness.Core.ImportCalls);
        Assert.Equal(V1, harness.Sources.Get(imported.ProfileId));
        Assert.Empty(harness.PendingHistoryFiles());
    }

    // ----------------------------------------------------------------------- history retention

    [Fact]
    public async Task History_RetainsOnlyTheTwentyNewestVersionsWithMatchingFilesAfterEveryImport()
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("many.yaml");
        File.WriteAllText(path, V1);
        ProfileImportResult first = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
        string profileId = first.ProfileId;
        ProfileHistoryEntry firstEntry = harness.Service.GetProfileHistory(profileId).Single();
        List<string> contents = [V1];

        for (int version = 2; version <= 27; version++)
        {
            string text = $"proxies: []\nrules:\n  - MATCH,DIRECT # version {version}\n";
            ProfileImportResult saved = await harness.Service.SaveAgentSourceAsync(
                Guid.NewGuid(), profileId, "many", text, ProfileCatalogService.AgentSourceHash(contents[^1]), () => true, CancellationToken.None);
            Assert.Equal(profileId, saved.ProfileId);
            contents.Add(text);

            IReadOnlyList<ProfileHistoryEntry> history = harness.Service.GetProfileHistory(profileId);
            Assert.Equal(Math.Min(version, 20), history.Count);
            Assert.Equal(Sha(text), history[0].ContentSha256);
            Assert.True(history.Select(static entry => entry.CreatedAt).SequenceEqual(history.Select(static entry => entry.CreatedAt).OrderDescending()));
            AssertConsistent(harness, profileId, text, $"version {version}");
        }

        Assert.DoesNotContain(harness.Service.GetProfileHistory(profileId), entry => entry.VersionId == firstEntry.VersionId);
        string[] expectedNewest = [.. contents.Skip(contents.Count - 20).Reverse().Select(Sha)];
        Assert.Equal(expectedNewest, harness.Service.GetProfileHistory(profileId).Select(static entry => entry.ContentSha256).ToArray());
    }

    [Fact]
    public async Task History_ObsoleteFileThatCannotBeDeletedNeverFailsACommittedImport()
    {
        await using CatalogHarness harness = new();
        string path = harness.Directory.Combine("locked-history.yaml");
        File.WriteAllText(path, V1);
        ProfileImportResult first = await harness.Service.ImportLocalProfileAsync(path, CancellationToken.None);
        string profileId = first.ProfileId;
        string oldest = Assert.Single(harness.HistoryFiles());
        string current = V1;
        for (int version = 2; version <= 20; version++)
        {
            string next = $"proxies: []\nrules: []\n# {version}\n";
            await harness.Service.SaveAgentSourceAsync(
                Guid.NewGuid(), profileId, "n", next, ProfileCatalogService.AgentSourceHash(current), () => true, CancellationToken.None);
            current = next;
        }

        Assert.Equal(20, harness.Service.GetProfileHistory(profileId).Count);
        await using (FileStream hold = new(oldest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string next = "proxies: []\nrules: []\n# 21\n";
            ProfileImportResult saved = await harness.Service.SaveAgentSourceAsync(
                Guid.NewGuid(), profileId, "n", next, ProfileCatalogService.AgentSourceHash(current), () => true, CancellationToken.None);
            Assert.Equal(profileId, saved.ProfileId);
            Assert.Equal(next, harness.Sources.Get(profileId));
        }

        Assert.Equal(20, harness.Service.GetProfileHistory(profileId).Count);
        Assert.Contains(harness.Log.Entries, entry => entry.Level == "Warning");
    }
}
