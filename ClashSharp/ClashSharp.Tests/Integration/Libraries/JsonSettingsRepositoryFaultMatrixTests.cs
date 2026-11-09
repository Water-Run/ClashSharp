using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Libraries;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration.Libraries;

/// <summary>
/// Faults at every persistence cut of the JSON settings repository, repeated over generated envelope chains: a
/// fresh process must always recover exactly the previous or the new envelope, and corrupt files must classify as
/// corrupt (recovering from the backup) rather than throwing.
/// </summary>
public sealed class JsonSettingsRepositoryFaultMatrixTests
{
    /// <summary>Faults of every kind at every declared cut leave exactly the old or the new envelope on disk.</summary>
    /// <param name="scenario">How much history exists before the faulted save.</param>
    [Theory]
    [InlineData(Scenario.InitialSave)]
    [InlineData(Scenario.UpdateWithoutBackup)]
    [InlineData(Scenario.UpdateWithBackup)]
    public async Task Save_FaultAtEveryCut_LeavesTheOldOrTheNewEnvelopeAndRecoversOnRetry(Scenario scenario)
    {
        foreach (SettingsPersistenceFaultPoint cut in Enum.GetValues<SettingsPersistenceFaultPoint>())
        {
            foreach (FaultKind kind in Enum.GetValues<FaultKind>())
            {
                await using Fixture fixture = new(new Random(SettingsGenerators.Seed + (int)cut * 10 + (int)kind));
                (SettingsEnvelope? previous, SettingsEnvelope next) = await fixture.PrepareAsync(scenario);
                Dictionary<string, int> hits = [];
                using CancellationTokenSource cancellation = new();
                fixture.Injector.Behavior = (point, token) =>
                {
                    hits[point.ToString()] = hits.GetValueOrDefault(point.ToString()) + 1;
                    if (point != cut)
                    {
                        return Task.CompletedTask;
                    }

                    switch (kind)
                    {
                        case FaultKind.Io: throw new IOException("injected");
                        case FaultKind.Unauthorized: throw new UnauthorizedAccessException("injected");
                        default:
                            cancellation.Cancel();
                            token.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                    }
                };

                SettingsPersistenceResult? saved = null;
                bool cancelled = false;
                try
                {
                    saved = await fixture.Repository.SaveAsync(next, previous?.EnvelopeRevision ?? 0, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                bool reachedCut = hits.ContainsKey(cut.ToString());
                if (!reachedCut)
                {
                    Assert.NotNull(saved);
                    Assert.True(saved.IsSucceeded);
                    continue;
                }

                if (kind == FaultKind.Cancel)
                {
                    Assert.True(cancelled || saved is { IsSucceeded: true }, $"{cut}: cancellation must propagate or complete");
                }
                else
                {
                    Assert.False(cancelled);
                    Assert.True(
                        saved is { IsSucceeded: true } or { Status: SettingsPersistenceStatus.Unavailable },
                        $"{cut}/{kind}: faults after the promotion point may still report success only if durable");
                }

                SettingsPersistenceResult reopened = await fixture.NewRepository().OpenAsync(CancellationToken.None);
                Assert.True(reopened.IsSucceeded, $"{cut}/{kind}: {reopened.Diagnostic?.Code}");
                string onDisk = reopened.Envelope is null ? "none" : Hash(reopened.Envelope);
                string[] legal = previous is null ? ["none", Hash(next)] : [Hash(previous), Hash(next)];
                Assert.Contains(onDisk, legal);
                Assert.Empty(Directory.EnumerateFiles(fixture.Repository.SettingsDirectoryPath, "*.candidate.*"));

                // A retry from the recovered revision always converges on the new envelope.
                long recoveredRevision = reopened.Envelope?.EnvelopeRevision ?? 0;
                fixture.Injector.Behavior = null;
                if (recoveredRevision != next.EnvelopeRevision)
                {
                    SettingsPersistenceResult retry = await fixture.NewRepository().SaveAsync(next, recoveredRevision, CancellationToken.None);
                    Assert.True(retry.IsSucceeded, $"{cut}/{kind}: {retry.Diagnostic?.Code}");
                }

                SettingsPersistenceResult final = await fixture.NewRepository().OpenAsync(CancellationToken.None);
                Assert.Equal(Hash(next), Hash(final.Envelope!));
            }
        }
    }

    /// <summary>Generated save chains with random faults always reopen to the last durable revision, monotonically.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SaveChain_WithRandomFaults_ReopensMonotonicallyAndNeverMixesEnvelopes(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        await using Fixture fixture = new(random);
        SettingsEnvelope current = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Assert.True((await fixture.Repository.SaveAsync(current, 0, CancellationToken.None)).IsSucceeded);
        SettingsEnvelopeEditor editor = new(SettingsRegistry.Default);
        IReadOnlyList<SettingDefinition> mutable = SettingsGenerators.MutableDefinitions(SettingsRegistry.Default);
        SettingsPersistenceFaultPoint[] cuts = Enum.GetValues<SettingsPersistenceFaultPoint>();
        long lastRevision = current.EnvelopeRevision;

        for (int step = 0; step < 30; step++)
        {
            SettingDefinition definition = mutable[random.Next(mutable.Count)];
            SettingsEnvelopeEditResult edit = editor.ApplyChanges(
                current,
                [new SettingValueChange(definition.Key, SettingsGenerators.Pick(random, definition))],
                SettingsGenerators.NextGuid(random));
            if (edit.Outcome != SettingsEnvelopeEditOutcome.Updated)
            {
                continue;
            }

            SettingsEnvelope next = edit.Envelope;
            bool inject = random.Next(3) != 0;
            SettingsPersistenceFaultPoint cut = cuts[random.Next(cuts.Length)];
            fixture.Injector.Behavior = inject
                ? (point, _) => point == cut ? throw new IOException("random") : Task.CompletedTask
                : null;

            SettingsPersistenceResult saved = await fixture.Repository.SaveAsync(next, current.EnvelopeRevision, CancellationToken.None);
            fixture.Injector.Behavior = null;
            SettingsPersistenceResult reopened = await fixture.NewRepository().OpenAsync(CancellationToken.None);

            Assert.True(reopened.IsSucceeded);
            Assert.NotNull(reopened.Envelope);
            Assert.True(reopened.Envelope.EnvelopeRevision >= lastRevision, "durable revision must never move backwards");
            Assert.Contains(reopened.Envelope.EnvelopeRevision, new[] { current.EnvelopeRevision, next.EnvelopeRevision });
            Assert.Equal(
                Hash(reopened.Envelope.EnvelopeRevision == next.EnvelopeRevision ? next : current),
                Hash(reopened.Envelope));
            if (saved.IsSucceeded)
            {
                Assert.Equal(next.EnvelopeRevision, reopened.Envelope.EnvelopeRevision);
            }

            lastRevision = reopened.Envelope.EnvelopeRevision;
            current = reopened.Envelope;
        }

        Assert.True(lastRevision > 5, "the chain must make real progress despite faults");
    }

    /// <summary>A corrupted primary (flip or truncation at sampled offsets) is replaced from the backup and never throws.</summary>
    [Fact]
    public async Task Open_CorruptPrimaryAtEveryOffset_RecoversFromTheBackupAndNeverThrows()
    {
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        await using Fixture fixture = new(new Random(SettingsGenerators.Seed), registry);
        SettingsEnvelope older = SettingsEnvelopeTestData.CreateMatchingEnvelope(registry);
        SettingDefinition flipped = registry.Get("LiveAppearance");
        SettingsEnvelope newer = new SettingsEnvelopeEditor(registry).ApplyChanges(
            older,
            [new SettingValueChange(flipped.Key, SettingsGenerators.Candidates(flipped).First(value => !value.Equals(flipped.DefaultValue)))],
            SettingsGenerators.NextGuid(new Random(SettingsGenerators.Seed))).Envelope;
        Assert.Equal(older.EnvelopeRevision + 1, newer.EnvelopeRevision);

        Assert.True((await fixture.Repository.SaveAsync(older, 0, CancellationToken.None)).IsSucceeded);
        Assert.True((await fixture.Repository.SaveAsync(newer, older.EnvelopeRevision, CancellationToken.None)).IsSucceeded);
        byte[] primary = await File.ReadAllBytesAsync(fixture.Repository.PrimaryPath);
        byte[] backup = await File.ReadAllBytesAsync(fixture.Repository.BackupPath);
        Assert.NotEqual(primary, backup);
        int recovered = 0;

        for (int offset = 0; offset < primary.Length; offset += 53)
        {
            foreach (byte[] corrupted in new[] { Flip(primary, offset), primary[..offset] })
            {
                await File.WriteAllBytesAsync(fixture.Repository.PrimaryPath, corrupted);
                SettingsPersistenceResult result = await fixture.NewRepository().OpenAsync(CancellationToken.None);

                Assert.True(result.IsSucceeded, $"offset {offset}: {result.Diagnostic?.Code}");
                Assert.NotNull(result.Envelope);
                if (result.RecoveredFromBackup)
                {
                    recovered++;
                    Assert.Equal(Hash(older, registry), Hash(result.Envelope, registry));
                    Assert.Equal(backup, await File.ReadAllBytesAsync(fixture.Repository.PrimaryPath));
                }
                else
                {
                    Assert.Equal(Hash(newer, registry), Hash(result.Envelope, registry));
                }

                foreach (string quarantined in Directory.EnumerateFiles(fixture.Repository.SettingsDirectoryPath, "*.corrupt.*"))
                {
                    File.Delete(quarantined);
                }

                await File.WriteAllBytesAsync(fixture.Repository.PrimaryPath, primary);
            }
        }

        Assert.True(recovered > primary.Length / 53, "nearly every corruption must be detected and repaired from the backup");
    }

    /// <summary>Invalid UTF-8 inside a JSON string of the primary (a typical single-sector corruption) must be recovered from the backup.</summary>
    [Fact]
    public async Task Open_InvalidUtf8InsideTheEnvelopeText_IsTreatedAsCorruptionNotAnUnhandledFailure()
    {
        await using Fixture fixture = new(new Random(SettingsGenerators.Seed));
        SettingsEnvelope first = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope second = SettingsEnvelopeTestData.CreatePendingEnvelope([("AppThemeMode", "Dark")]);
        Assert.True((await fixture.Repository.SaveAsync(first, 0, CancellationToken.None)).IsSucceeded);
        Assert.True((await fixture.Repository.SaveAsync(second, first.EnvelopeRevision, CancellationToken.None)).IsSucceeded);
        byte[] primary = await File.ReadAllBytesAsync(fixture.Repository.PrimaryPath);
        int payloadStart = System.Text.Encoding.UTF8.GetString(primary).IndexOf("\"payload\":\"", StringComparison.Ordinal) + "\"payload\":\"".Length;

        foreach (byte invalid in new byte[] { 0xFF, 0xC0, 0x80, 0xF2 })
        {
            byte[] corrupted = (byte[])primary.Clone();
            corrupted[payloadStart + 5] = invalid;
            await File.WriteAllBytesAsync(fixture.Repository.PrimaryPath, corrupted);

            SettingsPersistenceResult result = await fixture.NewRepository().OpenAsync(CancellationToken.None);

            Assert.True(result.IsSucceeded);
            Assert.True(result.RecoveredFromBackup);
            Assert.Equal(Hash(first), Hash(result.Envelope!));
            await File.WriteAllBytesAsync(fixture.Repository.PrimaryPath, primary);
            foreach (string quarantined in Directory.EnumerateFiles(fixture.Repository.SettingsDirectoryPath, "*.corrupt.*"))
            {
                File.Delete(quarantined);
            }
        }
    }

    /// <summary>Without a usable backup, a corrupt primary yields the typed corrupt result and is quarantined, never reinitialized.</summary>
    [Fact]
    public async Task Open_CorruptPrimaryWithoutBackup_IsQuarantinedAndNeverMasqueradesAsFirstRun()
    {
        await using Fixture fixture = new(new Random(SettingsGenerators.Seed));
        Assert.True((await fixture.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        byte[] primary = await File.ReadAllBytesAsync(fixture.Repository.PrimaryPath);
        Random random = new(SettingsGenerators.Seed);

        for (int round = 0; round < 8; round++)
        {
            await using Fixture isolated = new(random);
            Assert.True((await isolated.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
            await File.WriteAllBytesAsync(isolated.Repository.PrimaryPath, Flip(primary, random.Next(primary.Length)));

            SettingsPersistenceResult first = await isolated.NewRepository().OpenAsync(CancellationToken.None);
            SettingsPersistenceResult second = await isolated.NewRepository().OpenAsync(CancellationToken.None);

            Assert.Equal(SettingsPersistenceStatus.Corrupt, first.Status);
            Assert.Null(first.Envelope);
            Assert.Equal(SettingsPersistenceStatus.Corrupt, second.Status);
            Assert.NotEmpty(Directory.EnumerateFiles(isolated.Repository.SettingsDirectoryPath, "*.corrupt.*"));
            SettingsPersistenceResult save = await isolated.NewRepository().SaveAsync(
                SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None);
            Assert.Equal(SettingsPersistenceStatus.Corrupt, save.Status);
        }
    }

    /// <summary>Saving an invalid envelope or an impossible expected revision is rejected before any file is touched.</summary>
    [Fact]
    public async Task Save_InvalidRequests_AreRejectedBeforeAnyIo()
    {
        await using Fixture fixture = new(new Random(SettingsGenerators.Seed));
        SettingsEnvelope valid = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope invalid = new(valid.SchemaVersion, valid.EnvelopeRevision, valid.Desired.Take(1), valid.Applied.Take(1), [], []);

        foreach (long revision in new[] { -1L, long.MaxValue })
        {
            SettingsPersistenceResult result = await fixture.Repository.SaveAsync(valid, revision, CancellationToken.None);
            Assert.Equal(SettingsPersistenceStatus.Invalid, result.Status);
        }

        Assert.Equal(SettingsPersistenceStatus.Invalid, (await fixture.Repository.SaveAsync(invalid, 0, CancellationToken.None)).Status);
        Assert.False(File.Exists(fixture.Repository.PrimaryPath));
        Assert.False(Directory.Exists(fixture.Repository.SettingsDirectoryPath));
    }

    private static byte[] Flip(byte[] source, int offset)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] ^= 0x04;
        return copy;
    }

    private static string Hash(SettingsEnvelope envelope, SettingsRegistry? registry = null) =>
        SettingsEnvelopeCodec.Encode(envelope, registry ?? SettingsRegistry.Default).ContentHash;

    /// <summary>History that exists before the faulted save.</summary>
    public enum Scenario
    {
        /// <summary>No primary exists yet.</summary>
        InitialSave,

        /// <summary>One primary and no backup exist.</summary>
        UpdateWithoutBackup,

        /// <summary>Primary and backup both exist.</summary>
        UpdateWithBackup,
    }

    /// <summary>Kind of injected fault.</summary>
    public enum FaultKind
    {
        /// <summary>An IOException.</summary>
        Io,

        /// <summary>An UnauthorizedAccessException.</summary>
        Unauthorized,

        /// <summary>The caller token is cancelled at the cut.</summary>
        Cancel,
    }

    private sealed class ScriptedInjector : ISettingsPersistenceFaultInjector
    {
        public Func<SettingsPersistenceFaultPoint, CancellationToken, Task>? Behavior { get; set; }

        public Task InjectAsync(SettingsPersistenceFaultPoint faultPoint, CancellationToken cancellationToken) =>
            Behavior is null ? Task.CompletedTask : Behavior(faultPoint, cancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DataGenerationTestDirectory _directory = new();
        private readonly SettingsRegistry _registry;
        private readonly Random _random;

        public Fixture(Random random, SettingsRegistry? registry = null)
        {
            _random = random;
            _registry = registry ?? SettingsRegistry.Default;
            Descriptor = _directory.CreateGeneration(1);
            Repository = new JsonSettingsRepository(Descriptor, _registry, Injector);
        }

        public DataGenerationDescriptor Descriptor { get; }

        public ScriptedInjector Injector { get; } = new();

        public JsonSettingsRepository Repository { get; }

        public JsonSettingsRepository NewRepository() => new(Descriptor, _registry);

        public async Task<(SettingsEnvelope? Previous, SettingsEnvelope Next)> PrepareAsync(Scenario scenario)
        {
            SettingsEnvelope first = SettingsEnvelopeTestData.CreateMatchingEnvelope(_registry);
            SettingsEnvelopeEditor editor = new(_registry);
            SettingDefinition definition = SettingsGenerators.MutableDefinitions(_registry)[_random.Next(SettingsGenerators.MutableDefinitions(_registry).Count)];
            SettingValue changed = SettingsGenerators.Candidates(definition).First(value => !value.Equals(first.Desired[definition.Key].Value));
            SettingsEnvelope second = editor.ApplyChanges(first, [new SettingValueChange(definition.Key, changed)], SettingsGenerators.NextGuid(_random)).Envelope;
            SettingsEnvelopeEditResult thirdEdit = editor.ApplyChanges(
                second,
                [new SettingValueChange(definition.Key, SettingsGenerators.Candidates(definition).First(value => !value.Equals(changed)))],
                SettingsGenerators.NextGuid(_random));
            SettingsEnvelope third = thirdEdit.Envelope;
            switch (scenario)
            {
                case Scenario.InitialSave:
                    return (null, first);
                case Scenario.UpdateWithoutBackup:
                    Assert.True((await Repository.SaveAsync(first, 0, CancellationToken.None)).IsSucceeded);
                    return (first, second);
                default:
                    Assert.True((await Repository.SaveAsync(first, 0, CancellationToken.None)).IsSucceeded);
                    Assert.True((await Repository.SaveAsync(second, first.EnvelopeRevision, CancellationToken.None)).IsSucceeded);
                    return (second, third);
            }
        }

        public async ValueTask DisposeAsync() => await _directory.DisposeAsync();
    }
}
