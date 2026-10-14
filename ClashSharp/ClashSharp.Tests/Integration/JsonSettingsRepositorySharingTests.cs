using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Checks real sharing interruptions at the settings publication and flush boundaries.</summary>
public sealed class JsonSettingsRepositorySharingTests
{
    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeBackupPromotion)]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    public async Task SaveAsync_ShortReaderBlocksPromotion_CompletesTheSameRevision(SettingsPersistenceFaultPoint cut)
    {
        await using DataGenerationTestDirectory directory = new();
        var generation = directory.CreateGeneration(1);
        JsonSettingsRepository seed = new(generation, SettingsRegistry.Default);
        Assert.True((await seed.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        SettingsEnvelope current = SettingsEnvelopeTestData.CreatePendingEnvelope([("AppThemeMode", "Dark")]);
        Assert.True((await seed.SaveAsync(current, 1, CancellationToken.None)).IsSucceeded);
        SettingsEnvelope changed = new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(current,
            [new(SettingsRegistry.Keys.AppThemeMode, SettingsEnvelopeTestData.Value("AppThemeMode", "Light"))], Guid.NewGuid()).Envelope!;
        using ReadBlocker blocker = new(cut, cut == SettingsPersistenceFaultPoint.BeforeBackupPromotion ? seed.BackupPath : seed.PrimaryPath);
        JsonSettingsRepository repository = new(generation, SettingsRegistry.Default, blocker);
        Task<SettingsPersistenceResult> save = repository.SaveAsync(changed, current.EnvelopeRevision, CancellationToken.None);
        try
        {
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(40);
            Assert.False(save.IsCompleted);
        }
        finally { blocker.Release(); }

        SettingsPersistenceResult result = await save;
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        Assert.Equal(changed.EnvelopeRevision, result.Envelope!.EnvelopeRevision);
        Assert.Equal(SettingsEnvelopeCodec.Encode(changed, SettingsRegistry.Default).ContentHash,
            SettingsEnvelopeCodec.Encode((await seed.OpenAsync(CancellationToken.None)).Envelope!, SettingsRegistry.Default).ContentHash);
        Assert.Equal(1, blocker.BlockCount);
        Assert.Empty(Directory.GetFiles(seed.SettingsDirectoryPath, "*.candidate.*"));
    }

    [Fact]
    public async Task SaveAsync_PersistentReaderBlocksPromotion_ReturnsUnavailableAndKeepsTheOldPrimary()
    {
        await using DataGenerationTestDirectory directory = new();
        var generation = directory.CreateGeneration(1);
        JsonSettingsRepository seed = new(generation, SettingsRegistry.Default);
        Assert.True((await seed.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        byte[] before = await File.ReadAllBytesAsync(seed.PrimaryPath);
        using ReadBlocker blocker = new(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion, seed.PrimaryPath);
        JsonSettingsRepository repository = new(generation, SettingsRegistry.Default, blocker);

        SettingsPersistenceResult result = await repository.SaveAsync(
            SettingsEnvelopeTestData.CreatePendingEnvelope([("AppThemeMode", "Dark")]), 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SettingsPersistenceStatus.Unavailable, result.Status);
        blocker.Release();
        Assert.Equal(before, await File.ReadAllBytesAsync(seed.PrimaryPath));
        Assert.Empty(Directory.GetFiles(seed.SettingsDirectoryPath, "*.candidate.*"));
    }

    [Fact]
    public async Task SaveAsync_CancelledDuringSharingWait_DoesNotPromoteTheCandidate()
    {
        await using DataGenerationTestDirectory directory = new();
        var generation = directory.CreateGeneration(1);
        JsonSettingsRepository seed = new(generation, SettingsRegistry.Default);
        Assert.True((await seed.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        byte[] before = await File.ReadAllBytesAsync(seed.PrimaryPath);
        using CancellationTokenSource caller = new();
        using ReadBlocker blocker = new(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion, seed.PrimaryPath);
        JsonSettingsRepository repository = new(generation, SettingsRegistry.Default, blocker);
        Task<SettingsPersistenceResult> save = repository.SaveAsync(
            SettingsEnvelopeTestData.CreatePendingEnvelope([("AppThemeMode", "Dark")]), 1, caller.Token);
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        blocker.Release();
        Assert.Equal(before, await File.ReadAllBytesAsync(seed.PrimaryPath));
        Assert.Empty(Directory.GetFiles(seed.SettingsDirectoryPath, "*.candidate.*"));
    }

    [Fact]
    public async Task FlushPromotedFileAsync_ShortReaderDeniesWrite_ReleasesItsHandleWithoutChangingBytes()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = new(directory.CreateGeneration(1), SettingsRegistry.Default);
        Assert.True((await repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        byte[] before = await File.ReadAllBytesAsync(repository.PrimaryPath);
        using FileStream reader = new(repository.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task flush = JsonSettingsRepository.FlushPromotedFileAsync(repository.PrimaryPath);
        try
        {
            await Task.Delay(40);
            Assert.False(flush.IsCompleted);
        }
        finally { reader.Dispose(); }
        await flush;
        Assert.Equal(before, await File.ReadAllBytesAsync(repository.PrimaryPath));
        using FileStream exclusive = new(repository.PrimaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlushPromotedFileAsync_PersistentReader_ReturnsTheSharingFailureWithinItsBound()
    {
        await using DataGenerationTestDirectory directory = new();
        JsonSettingsRepository repository = new(directory.CreateGeneration(1), SettingsRegistry.Default);
        Assert.True((await repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
        using FileStream reader = new(repository.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            JsonSettingsRepository.FlushPromotedFileAsync(repository.PrimaryPath).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True((failure.HResult & 0xFFFF) is 32 or 33 || failure.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 });
        reader.Dispose();
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
    }

    private sealed class ReadBlocker(SettingsPersistenceFaultPoint cut, string path) : ISettingsPersistenceFaultInjector, IDisposable
    {
        private FileStream? _reader;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BlockCount { get; private set; }
        public Task InjectAsync(SettingsPersistenceFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == cut && BlockCount == 0)
            {
                _reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                ++BlockCount;
                Entered.TrySetResult();
            }
            return Task.CompletedTask;
        }
        public void Release() => Interlocked.Exchange(ref _reader, null)?.Dispose();
        public void Dispose() => Release();
    }
}
