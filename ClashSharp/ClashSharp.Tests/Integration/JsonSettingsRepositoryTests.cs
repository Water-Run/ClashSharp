using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies generation-pinned settings persistence and optimistic replacement.</summary>
public sealed class JsonSettingsRepositoryTests
{
    /// <summary>Verifies an untouched generation opens as one successful empty repository.</summary>
    [Fact]
    public async Task OpenAsync_EmptyGeneration_ReturnsSuccessfulEmptyResult()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Succeeded, result.Status);
        Assert.Null(result.Envelope);
        Assert.False(result.RecoveredFromBackup);
    }

    /// <summary>Verifies initial save and reopen preserve the exact canonical envelope.</summary>
    [Fact]
    public async Task SaveAsync_InitialEnvelope_RoundTrips()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        SettingsEnvelope envelope = SettingsEnvelopeTestData.CreateMatchingEnvelope();

        SettingsPersistenceResult saved = await repository.SaveAsync(
            envelope,
            expectedRevision: 0,
            CancellationToken.None);
        SettingsPersistenceResult loaded =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Succeeded, saved.Status);
        Assert.Equal(SettingsPersistenceStatus.Succeeded, loaded.Status);
        Assert.Equal(envelope.EnvelopeRevision, loaded.Envelope!.EnvelopeRevision);
        Assert.Equal(
            SettingsEnvelopeCodec.Encode(envelope, SettingsRegistry.Default).ContentHash,
            SettingsEnvelopeCodec.Encode(loaded.Envelope, SettingsRegistry.Default).ContentHash);
    }

    /// <summary>Verifies stale optimistic writers receive the verified current envelope.</summary>
    [Fact]
    public async Task SaveAsync_StaleExpectedRevision_ReturnsConflictWithoutMutation()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        SettingsEnvelope first = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope second = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        await repository.SaveAsync(first, 0, CancellationToken.None);

        SettingsPersistenceResult conflict = await repository.SaveAsync(
            second,
            expectedRevision: 0,
            CancellationToken.None);
        SettingsPersistenceResult loaded =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Conflict, conflict.Status);
        Assert.Equal(first.EnvelopeRevision, conflict.Envelope!.EnvelopeRevision);
        Assert.Equal(first.EnvelopeRevision, loaded.Envelope!.EnvelopeRevision);
    }

    /// <summary>Verifies exactly one cross-instance writer can advance one expected revision.</summary>
    [Fact]
    public async Task SaveAsync_ConcurrentInstances_ExactlyOneWriterAdvances()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
        JsonSettingsRepository firstRepository = CreateRepository(descriptor);
        JsonSettingsRepository secondRepository = CreateRepository(descriptor);
        SettingsEnvelope initial = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        await firstRepository.SaveAsync(initial, 0, CancellationToken.None);
        SettingsEnvelope dark = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        SettingsEnvelope light = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Light")]);

        SettingsPersistenceResult[] results = await Task.WhenAll(
            firstRepository.SaveAsync(dark, 1, CancellationToken.None),
            secondRepository.SaveAsync(light, 1, CancellationToken.None));

        Assert.Single(results, static result =>
            result.Status == SettingsPersistenceStatus.Succeeded);
        Assert.Single(results, static result =>
            result.Status == SettingsPersistenceStatus.Conflict);
        SettingsPersistenceResult loaded =
            await firstRepository.OpenAsync(CancellationToken.None);
        Assert.Equal(2, loaded.Envelope!.EnvelopeRevision);
    }

    /// <summary>Verifies invalid domain envelopes never reach a candidate file.</summary>
    [Fact]
    public async Task SaveAsync_InvalidEnvelope_ReturnsStableInvalidResult()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        SettingsEnvelope valid = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, SettingDesiredEntry> desired = valid.Desired.ToDictionary();
        desired.Remove(SettingsEnvelopeTestData.Key("AppThemeMode"));
        SettingsEnvelope invalid = new(
            valid.SchemaVersion,
            valid.EnvelopeRevision,
            desired,
            valid.Applied,
            valid.PendingApplications,
            valid.MigrationHistory);

        SettingsPersistenceResult result = await repository.SaveAsync(
            invalid,
            expectedRevision: 0,
            CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Invalid, result.Status);
        Assert.Equal("settings.envelope.desired.missing", result.Diagnostic!.Code);
        Assert.False(File.Exists(repository.PrimaryPath));
    }

    /// <summary>Verifies cancellation while waiting for the cross-instance writer lock escapes as cancellation.</summary>
    [Fact]
    public async Task SaveAsync_CancelledWhileWriterLockHeld_DoesNotWrite()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        repository.EnsureLayout();
        await using FileStream heldLock = new(
            ReparseSafeFile.OpenWriteLock(repository.LockPath),
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);
        using CancellationTokenSource cancellationSource =
            new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.SaveAsync(
                SettingsEnvelopeTestData.CreateMatchingEnvelope(),
                expectedRevision: 0,
                cancellationSource.Token));

        Assert.False(File.Exists(repository.PrimaryPath));
    }

    /// <summary>Verifies denied and busy-like write failures are returned as unavailable diagnostics.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_InjectedIoFailure_ReturnsUnavailable(bool unauthorized)
    {
        await using DataGenerationTestDirectory directory = new();
        ISettingsFaultFactory faultFactory = unauthorized
            ? new UnauthorizedFaultFactory()
            : new IoFaultFactory();
        JsonSettingsRepository repository = new(
            directory.CreateGeneration(1),
            SettingsRegistry.Default,
            faultFactory.Create());

        SettingsPersistenceResult result = await repository.SaveAsync(
            SettingsEnvelopeTestData.CreateMatchingEnvelope(),
            expectedRevision: 0,
            CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Unavailable, result.Status);
        Assert.Equal("settings.persistence.unavailable", result.Diagnostic!.Code);
    }

    /// <summary>Verifies a real sharing denial is unavailable rather than an empty repository.</summary>
    [Fact]
    public async Task OpenAsync_PrimaryHeldExclusively_ReturnsUnavailable()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        SettingsPersistenceResult saved = await repository.SaveAsync(
            SettingsEnvelopeTestData.CreateMatchingEnvelope(),
            expectedRevision: 0,
            CancellationToken.None);
        Assert.True(saved.IsSucceeded, saved.Diagnostic?.Code);
        await using FileStream heldPrimary = new(
            repository.PrimaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Unavailable, result.Status);
        Assert.Equal("settings.persistence.unavailable", result.Diagnostic!.Code);
    }

    /// <summary>Verifies a dangling reparse point cannot masquerade as an absent primary.</summary>
    [Fact]
    public async Task OpenAsync_DanglingPrimarySymlink_ReturnsUnavailable()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        repository.EnsureLayout();
        string missingTarget = Path.Combine(
            directory.RootPath,
            "missing-settings-envelope.json");
        File.CreateSymbolicLink(repository.PrimaryPath, missingTarget);

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }

    /// <summary>Verifies a non-file primary entry is unavailable rather than absent.</summary>
    [Fact]
    public async Task OpenAsync_PrimaryPathOccupiedByDirectory_ReturnsUnavailable()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        repository.EnsureLayout();
        Directory.CreateDirectory(repository.PrimaryPath);
        Assert.False(File.Exists(repository.PrimaryPath));

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Unavailable, result.Status);
        Assert.Null(result.Envelope);
    }

    /// <summary>Verifies a corrupt primary is quarantined and restored from a valid backup.</summary>
    [Fact]
    public async Task OpenAsync_CorruptPrimaryWithValidBackup_RecoversPriorEnvelope()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        SettingsEnvelope first = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope second = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        await repository.SaveAsync(first, 0, CancellationToken.None);
        await repository.SaveAsync(second, 1, CancellationToken.None);
        await File.WriteAllTextAsync(repository.PrimaryPath, "{broken");

        SettingsPersistenceResult recovered =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Succeeded, recovered.Status);
        Assert.True(recovered.RecoveredFromBackup);
        Assert.Equal(first.EnvelopeRevision, recovered.Envelope!.EnvelopeRevision);
        Assert.NotEmpty(Directory.EnumerateFiles(
            repository.SettingsDirectoryPath,
            "*.corrupt.*"));
        SettingsPersistenceResult reopened =
            await repository.OpenAsync(CancellationToken.None);
        Assert.Equal(first.EnvelopeRevision, reopened.Envelope!.EnvelopeRevision);
    }

    /// <summary>Verifies cancellation before recovery promotion leaves backup authority retryable.</summary>
    [Fact]
    public async Task OpenAsync_CancelledBeforeRecoveryPromotion_DoesNotPromote()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor descriptor = directory.CreateGeneration(1);
        JsonSettingsRepository setupRepository = CreateRepository(descriptor);
        SettingsEnvelope first = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        SettingsEnvelope second = SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark")]);
        await setupRepository.SaveAsync(first, 0, CancellationToken.None);
        await setupRepository.SaveAsync(second, 1, CancellationToken.None);
        await File.WriteAllTextAsync(setupRepository.PrimaryPath, "{broken");
        using CancellationTokenSource cancellationSource = new();
        JsonSettingsRepository cancellingRepository = new(
            descriptor,
            SettingsRegistry.Default,
            new CancellingFaultInjector(
                SettingsPersistenceFaultPoint.BeforeEnvelopePromotion,
                cancellationSource));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancellingRepository.OpenAsync(cancellationSource.Token));

        Assert.False(File.Exists(cancellingRepository.PrimaryPath));
        Assert.True(File.Exists(cancellingRepository.BackupPath));
        SettingsPersistenceResult recovered =
            await CreateRepository(descriptor).OpenAsync(CancellationToken.None);
        Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
        Assert.Equal(first.EnvelopeRevision, recovered.Envelope!.EnvelopeRevision);
    }

    /// <summary>Verifies invalid primary and backup are quarantined and reported as corrupt.</summary>
    [Fact]
    public async Task OpenAsync_CorruptPrimaryAndBackup_ReturnsCorrupt()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        repository.EnsureLayout();
        await File.WriteAllTextAsync(repository.PrimaryPath, "{broken-primary");
        await File.WriteAllTextAsync(repository.BackupPath, "{broken-backup");

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Corrupt, result.Status);
        Assert.Equal("settings.persistence.primary_and_backup_corrupt", result.Diagnostic!.Code);
        Assert.False(File.Exists(repository.PrimaryPath));
        Assert.False(File.Exists(repository.BackupPath));
    }

    /// <summary>Verifies open removes abandoned same-directory candidates without touching other files.</summary>
    [Fact]
    public async Task OpenAsync_OrphanCandidates_CleansOnlyKnownCandidatePrefix()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = CreateRepository(directory.CreateGeneration(1));
        repository.EnsureLayout();
        string primaryCandidate = repository.PrimaryPath + ".candidate.orphan";
        string backupCandidate = repository.BackupPath + ".candidate.orphan";
        string unrelated = Path.Combine(repository.SettingsDirectoryPath, "keep.txt");
        await File.WriteAllTextAsync(primaryCandidate, "partial");
        await File.WriteAllTextAsync(backupCandidate, "partial");
        await File.WriteAllTextAsync(unrelated, "keep");

        SettingsPersistenceResult result =
            await repository.OpenAsync(CancellationToken.None);

        Assert.Equal(SettingsPersistenceStatus.Succeeded, result.Status);
        Assert.False(File.Exists(primaryCandidate));
        Assert.False(File.Exists(backupCandidate));
        Assert.True(File.Exists(unrelated));
    }

    private static JsonSettingsRepository CreateRepository(
        DataGenerationDescriptor descriptor)
    {
        return new JsonSettingsRepository(
            descriptor,
            SettingsRegistry.Default);
    }

    private interface ISettingsFaultFactory
    {
        ISettingsPersistenceFaultInjector Create();
    }

    private sealed class UnauthorizedFaultFactory : ISettingsFaultFactory
    {
        public ISettingsPersistenceFaultInjector Create() =>
            new ThrowingFaultInjector(new UnauthorizedAccessException("Denied."));
    }

    private sealed class IoFaultFactory : ISettingsFaultFactory
    {
        public ISettingsPersistenceFaultInjector Create() =>
            new ThrowingFaultInjector(new IOException("Busy."));
    }

    private sealed class ThrowingFaultInjector(Exception failure)
        : ISettingsPersistenceFaultInjector
    {
        public Task InjectAsync(
            SettingsPersistenceFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancellingFaultInjector(
        SettingsPersistenceFaultPoint selectedFaultPoint,
        CancellationTokenSource cancellationSource)
        : ISettingsPersistenceFaultInjector
    {
        public Task InjectAsync(
            SettingsPersistenceFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == selectedFaultPoint)
            {
                cancellationSource.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
