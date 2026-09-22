extern alias ClashSharpUi;

using System.Collections.Concurrent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;
using AppThemeService = ClashSharpUi::ClashSharp.Service.AppThemeService;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises real generation authority, installed appearance policies and the production accent palette with isolated UI ports.</summary>
public sealed class AppearanceSettingsParticipantTests
{
    [Fact]
    public async Task Construction_ExposesImmutablePolicyDefaultsWithoutReadingTheWindowOrStorage()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        using NativeSurface native = new();
        string[] before = Directory.GetFileSystemEntries(directory.RootPath, "*", SearchOption.AllDirectories);
        await using AppearanceSettingsParticipant participant = new(generation, new(), SettingsRegistry.Default, native.CreateDispatcher(), native);
        IAppearanceSettingsReader reader = participant;
        AppearanceSettingsSnapshot snapshot = reader.CaptureSnapshot();
        Assert.Same(generation, snapshot.Generation);
        Assert.Equal(6, snapshot.Values.Count);
        Assert.False(snapshot.Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.Throws<KeyNotFoundException>(() => snapshot.Get<AppThemeMode>(SettingsRegistry.Keys.AppThemeMode));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<SettingKey, SettingValue>)snapshot.Values).Clear());
        Assert.Equal(0, native.Reads);
        Assert.Equal(0, native.Writes);
        Assert.Equal(0, native.ResourceReads);
        Assert.Equal(before, Directory.GetFileSystemEntries(directory.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CompleteBatch_InstallsAllTenKeysAfterRunningIsDurableAndPreservesHistoricalPolicies()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceSettingsSnapshot before = fixture.Current();
        SettingValueChange[] changes =
        [Change("DisplayLanguage", "English"), Change("AppThemeMode", "Dark"), Change("AppAccentColorMode", "Custom"),
            Change("AppAccentColorValue", "#804477AA"), Change("TrayUseMonochromeInactiveIcon", "true"),
            Change("TrayVisibleFeatureIds", "settings,safe-exit"), Change("MainlandChinaFeatureMode", "Disabled"),
            Change("MainlandChinaUrlBlockingEnabled", "true"), Change("MasterHeroStatusLayout", ReverseDefault("MasterHeroStatusLayout")),
            Change("MasterInfoTileLayout", ReverseDefault("MasterInfoTileLayout"))];
        Assert.Equal(SettingsRegistry.Default.Definitions.Where(definition => definition.ApplicationKind == SettingApplicationKind.Appearance)
            .Select(definition => definition.Key).OrderBy(key => key.Value), changes.Select(change => change.Key).OrderBy(key => key.Value));
        Assert.True((await fixture.ChangeAsync(changes)).IsSucceeded);
        Assert.Same(before, fixture.Current());
        Assert.Equal(0, fixture.Native.Writes);
        CapturingParticipant wrapper = new(fixture.Participant)
        {
            BeforeApply = async () =>
            {
                Assert.Same(before, fixture.Current());
                Assert.Equal(SettingsApplicationBatchState.Running,
                    Assert.Single((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications).State);
            },
        };
        SettingsAuthorityResult applied = await fixture.ApplyAsync(SettingsRegistry.Keys.AppThemeMode, wrapper);
        Assert.True(applied.IsSucceeded, applied.Code);
        Assert.Empty(applied.Envelope!.PendingApplications);
        foreach (SettingValueChange change in changes) { Assert.Equal(change.Value, applied.Envelope.Applied[change.Key].Value); }
        AppearanceNativeConfiguration actual = await fixture.ReadNativeAsync();
        Assert.Equal(AppLanguage.English, actual.Language);
        Assert.Equal(AppThemeMode.Dark, actual.Theme);
        Assert.Equal(new(AppAccentColorMode.Custom, "#804477AA"), actual.Accent);
        Assert.Equal(48, fixture.Native.Resources.Values.Count);
        Assert.Equal(0x804477AAu, fixture.Native.Resources.Values["SystemAccentColor"].Argb);
        Assert.True(fixture.Current().Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.False(before.Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.NotSame(before, fixture.Current());
    }

    [Fact]
    public async Task SingleAccentKey_UsesTheInstalledCompanionAndLeavesOtherDesiredIntentPending()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("AppAccentColorValue", "#804477AA"))).IsSucceeded);
        SettingsAuthorityResult applied = await fixture.Authority.ApplyChangesAsync([Change("AppAccentColorMode", "Custom")], Guid.NewGuid(), CancellationToken.None);
        Assert.True(applied.IsSucceeded, applied.Code);
        Assert.Equal(new(AppAccentColorMode.Custom, "#FF0078D4"), (await fixture.ReadNativeAsync()).Accent);
        Assert.Equal(SettingsRegistry.Keys.AppAccentColorValue, Assert.Single(Assert.Single(applied.Envelope!.PendingApplications).Entries).Key);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.AppAccentColorValue)).IsSucceeded);
        Assert.Equal(new(AppAccentColorMode.Custom, "#804477AA"), (await fixture.ReadNativeAsync()).Accent);
    }

    [Fact]
    public async Task PolicyOnlyBatch_PreservesUnappliedNativeIntentAndDoesNotWriteTheWindow()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("AppThemeMode", "Dark"))).IsSucceeded);
        SettingsAuthorityResult applied = await fixture.Authority.ApplyChangesAsync([Change("MasterInfoTileLayout", ReverseDefault("MasterInfoTileLayout"))], Guid.NewGuid(), CancellationToken.None);
        Assert.True(applied.IsSucceeded, applied.Code);
        Assert.Equal(0, fixture.Native.Writes);
        Assert.Equal(AppThemeMode.FollowSystem, (await fixture.ReadNativeAsync()).Theme);
        Assert.Equal(ReverseDefault("MasterInfoTileLayout"), fixture.Current().Get<string>(SettingsRegistry.Keys.MasterInfoTileLayout));
        Assert.Equal(SettingsRegistry.Keys.AppThemeMode, Assert.Single(Assert.Single(applied.Envelope!.PendingApplications).Entries).Key);
    }

    [Fact]
    public async Task MissingWindow_DoesNotPublishPoliciesAndCanBeRetriedWhenTheWindowReturns()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceSettingsSnapshot before = fixture.Current();
        fixture.Native.Unavailable = true;
        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync([Change("TrayVisibleFeatureIds", "safe-exit")], Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        Assert.Same(before, fixture.Current());
        Assert.Equal(0, fixture.Native.Writes);
        Assert.Equal(SettingAppliedStateKind.Unknown, failed.Envelope!.Applied[SettingsRegistry.Keys.TrayVisibleFeatureIds].Kind);
        fixture.Native.Unavailable = false;
        Assert.True((await fixture.RetryAsync(failed)).IsSucceeded);
        Assert.Equal("safe-exit", fixture.Current().Get<string>(SettingsRegistry.Keys.TrayVisibleFeatureIds));
    }

    [Theory]
    [InlineData("partial-accent")]
    [InlineData("theme-before-write")]
    public async Task InterruptedNativeApplication_RestoresTheObservedBaselineBeforeExplicitRetry(string fault)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceNativeConfiguration before = await fixture.ReadNativeAsync();
        AppearanceSettingsSnapshot policies = fixture.Current();
        if (fault == "partial-accent") { fixture.Native.Resources.FailAfterWrite = 2; }
        else { fixture.Native.FailNextTheme = true; }
        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("AppAccentColorMode", "Custom"), Change("AppAccentColorValue", "#804477AA"),
                Change("AppThemeMode", "Dark"), Change("TrayUseMonochromeInactiveIcon", "true")], Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        Assert.Equal(before, await fixture.ReadNativeAsync());
        Assert.Empty(fixture.Native.Resources.Values);
        Assert.Same(policies, fixture.Current());
        Assert.True((await fixture.RetryAsync(failed)).IsSucceeded);
        Assert.Equal(AppThemeMode.Dark, (await fixture.ReadNativeAsync()).Theme);
        Assert.True(fixture.Current().Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
    }

    [Fact]
    public async Task LostFinalNativeReply_IsAcceptedOnlyAfterIndependentTargetObservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Native.LoseNextThemeReply = true;
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync(
            [Change("AppThemeMode", "Dark"), Change("TrayUseMonochromeInactiveIcon", "true")], Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal(AppThemeMode.Dark, (await fixture.ReadNativeAsync()).Theme);
        Assert.True(fixture.Current().Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.Equal(1, fixture.Native.Writes);
    }

    [Fact]
    public async Task LostParticipantReply_IsResolvedByTheAuthorityFromActualNativeAndPolicyState()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("AppThemeMode", "Dark"), Change("TrayUseMonochromeInactiveIcon", "true"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.AppThemeMode, new(fixture.Participant) { LoseReply = true });
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        Assert.True(fixture.Current().Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
    }

    [Fact]
    public async Task IgnoredNativeWrites_CannotPublishPolicyChangesOrClearThePendingBatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceSettingsSnapshot before = fixture.Current();
        fixture.Native.IgnoreThemeWrites = true;
        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("AppThemeMode", "Dark"), Change("TrayUseMonochromeInactiveIcon", "true")], Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        Assert.Same(before, fixture.Current());
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(failed.Envelope!.PendingApplications).State);
    }

    [Fact]
    public async Task NestedFatalFailure_EscapesWithoutCompensationOrPolicyPublication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceSettingsSnapshot before = fixture.Current();
        Exception fatal = new AggregateException(new InvalidOperationException("Isolated nested fatal.", Activator.CreateInstance<OutOfMemoryException>()));
        fixture.Native.ThemeFailure = fatal;
        Exception? observed = await Record.ExceptionAsync(() => fixture.Authority.ApplyChangesAsync(
            [Change("AppAccentColorMode", "Custom"), Change("AppThemeMode", "Dark"), Change("TrayVisibleFeatureIds", "safe-exit")], Guid.NewGuid(), CancellationToken.None));
        Assert.Same(fatal, observed);
        Assert.Same(before, fixture.Current());
        Assert.Equal(2, fixture.Native.Writes);
        Assert.Equal(48, fixture.Native.Resources.Values.Count);
        Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications).State);
    }

    [Fact]
    public async Task ForeignGenerationAndAdmission_AreRejectedBeforePlatformAccess()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("AppThemeMode", "Dark"))).IsSucceeded);
        CapturingParticipant capture = new(fixture.Participant);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.AppThemeMode, capture)).IsSucceeded);
        await using AppearanceSettingsParticipant other = new(fixture.Directory.CreateGeneration(2), fixture.Admission,
            SettingsRegistry.Default, fixture.Native.CreateDispatcher(), fixture.Native);
        int reads = fixture.Native.Reads;
        int writes = fixture.Native.Writes;
        using MutationAdmissionLease own = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ApplyAsync(capture.Request!, own, CancellationToken.None));
        using MutationAdmissionLease foreign = new MutationAdmissionBarrier().AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(capture.Request!, foreign, CancellationToken.None));
        Assert.Equal(reads, fixture.Native.Reads);
        Assert.Equal(writes, fixture.Native.Writes);
    }

    [Fact]
    public async Task GenerationReplacement_RetiresTheOldReaderAndUsesANewPolicyOwnerWithTheSameWindow()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.ApplyChangesAsync([Change("AppThemeMode", "Dark"), Change("TrayUseMonochromeInactiveIcon", "true")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        AppearanceSettingsSnapshot historical = fixture.Current();
        DataGenerationTransition transition = await fixture.Generations.BeginDrainAsync(fixture.Generations.CurrentManifest.ContentHash, CancellationToken.None);
        DataGenerationDescriptor generation = fixture.Directory.CreateGeneration(2);
        Lifetime next = await Lifetime.CreateAsync(generation, fixture.Admission, fixture.Native);
        transition.Stage(new(generation, next));
        await transition.PromoteManifestAsync(fixture.Directory.Store, CancellationToken.None);
        transition.SwapToPromoted();
        await transition.CommitAsync();
        Assert.Throws<ObjectDisposedException>(() => fixture.Participant.CaptureSnapshot());
        Assert.True(historical.Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.False(fixture.Current().Get<bool>(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon));
        Assert.Equal(generation.GenerationId, fixture.Current().Generation.GenerationId);
        Assert.Equal(AppThemeMode.Dark, (await next.Dispatcher.InvokeAsync(fixture.Native.CaptureConfiguration, CancellationToken.None)).Theme);
        Assert.True((await fixture.Authority.ApplyChangesAsync([Change("TrayVisibleFeatureIds", "safe-exit")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        Assert.Equal("safe-exit", fixture.Current().Get<string>(SettingsRegistry.Keys.TrayVisibleFeatureIds));
        Assert.True((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.Desired[SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon].Value.Get<bool>());
    }

    [Fact]
    public async Task GenerationDrain_WaitsForTheQueuedUiCallbackAndFinalDurableApplicationDespitePageCancellation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource page = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        fixture.Native.HoldQueue = true;
        Task<SettingsAuthorityResult> command = fixture.Authority.ApplyChangesAsync(
            [Change("AppThemeMode", "Dark"), Change("TrayVisibleFeatureIds", "safe-exit")], Guid.NewGuid(), page.Token);
        Task<DataGenerationTransition>? drain = null;
        try
        {
            await fixture.Native.Queued.Task.WaitAsync(deadline.Token);
            Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single((await fixture.Repository.OpenAsync(deadline.Token)).Envelope!.PendingApplications).State);
            drain = fixture.Generations.BeginDrainAsync(fixture.Generations.CurrentManifest.ContentHash, deadline.Token).AsTask();
            page.Cancel();
            Assert.False(command.IsCompleted);
            Assert.False(drain.IsCompleted);
        }
        finally
        {
            fixture.Native.HoldQueue = false;
            fixture.Native.DrainQueue();
            await command.WaitAsync(deadline.Token);
        }
        Assert.True((await command).IsSucceeded);
        DataGenerationTransition transition = await drain!;
        Assert.Empty((await fixture.Repository.OpenAsync(deadline.Token)).Envelope!.PendingApplications);
        Assert.Equal(AppThemeMode.Dark, (await fixture.ReadNativeAsync()).Theme);
        await transition.AbortAsync();
        Assert.Equal("safe-exit", fixture.Current().Get<string>(SettingsRegistry.Keys.TrayVisibleFeatureIds));
    }

    [Fact]
    public async Task Startup_ReobservesTheNewWindowAndPolicyOwnerUnderExclusiveAdmission()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.ApplyChangesAsync(
            [Change("AppThemeMode", "Dark"), Change("AppAccentColorMode", "Custom"), Change("TrayVisibleFeatureIds", "safe-exit")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        await fixture.Participant.DisposeAsync();
        using NativeSurface newWindow = new();
        await using AppearanceSettingsParticipant reopened = new(fixture.Lifetime.Session.Generation, fixture.Admission,
            SettingsRegistry.Default, newWindow.CreateDispatcher(), newWindow);
        await using MutationAdmissionLease startup = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        SettingsAuthorityResult prepared = await fixture.Lifetime.Session.PrepareStartupAdmittedAsync(Guid.NewGuid(), startup, CancellationToken.None);
        SettingsApplicationBatch batch = Assert.Single(prepared.Envelope!.PendingApplications, item => item.ApplicationKind == SettingApplicationKind.Appearance);
        CapturingParticipant capture = new(reopened);
        SettingsAuthorityResult result = await fixture.Lifetime.Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, capture,
            SettingsApplicationPhase.Startup, startup, CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("safe-exit", reopened.CaptureSnapshot().Get<string>(SettingsRegistry.Keys.TrayVisibleFeatureIds));
        Assert.Equal(48, newWindow.Resources.Values.Count);
        Assert.Equal(SettingAppliedValueSource.StartupReconciliation, result.Envelope!.Applied[SettingsRegistry.Keys.AppThemeMode].Source);
        await startup.DisposeAsync();
        using MutationAdmissionLease ordinary = fixture.Admission.AcquireOrdinary();
        int reads = newWindow.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ApplyAsync(capture.Request!, ordinary, CancellationToken.None));
        Assert.Equal(reads, newWindow.Reads);
    }

    [Fact]
    public async Task UnverifiableCompensation_RemainsUnknownAndCannotAuthorizeBlindRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppearanceSettingsSnapshot before = fixture.Current();
        fixture.Native.Resources.FailAfterWrite = 2;
        fixture.Native.Resources.FailRemovals = true;
        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("AppAccentColorMode", "Custom"), Change("TrayUseMonochromeInactiveIcon", "true")], Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        Assert.Equal(SettingAppliedStateKind.Unknown, failed.Envelope!.Applied[SettingsRegistry.Keys.AppAccentColorMode].Kind);
        Assert.Same(before, fixture.Current());
        int writes = fixture.Native.Writes;
        SettingsAuthorityResult retry = await fixture.RetryAsync(failed);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, retry.Status);
        Assert.Equal("settings.application.probe_failed", retry.Code);
        Assert.Equal(writes, fixture.Native.Writes);
        Assert.Same(before, fixture.Current());
    }

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));
    private static string ReverseDefault(string key) => string.Join(',', SettingsRegistry.Default.Get(key).DefaultValue.Get<string>().Split(',').Reverse());

    private sealed class Fixture : IAsyncDisposable
    {
        public DataGenerationTestDirectory Directory { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public DataGenerationManager Generations { get; } = new();
        public NativeSurface Native { get; } = new();
        public Lifetime Lifetime { get; private set; } = null!;
        public AppearanceSettingsParticipant Participant => Lifetime.Participant;
        public JsonSettingsRepository Repository => Lifetime.Repository;
        public GenerationSettingsAuthority Authority { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                DataGenerationManifestSnapshot manifest = await fixture.Directory.PromoteFirstAsync();
                fixture.Lifetime = await AppearanceSettingsParticipantTests.Lifetime.CreateAsync(manifest.Descriptor, fixture.Admission, fixture.Native);
                fixture.Generations.Initialize(manifest, new(manifest.Descriptor, fixture.Lifetime));
                fixture.Authority = new(fixture.Generations, fixture.Admission);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public AppearanceSettingsSnapshot Current() => Generations.ReadSnapshot<IAppearanceSettingsReader, AppearanceSettingsSnapshot>((reader, _) => reader.CaptureSnapshot());
        public Task<AppearanceNativeConfiguration> ReadNativeAsync() => Lifetime.Dispatcher.InvokeAsync(Native.CaptureConfiguration, CancellationToken.None);
        public async Task<SettingsAuthorityResult> ChangeAsync(params SettingValueChange[] changes)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            return await Lifetime.Session.ChangeAdmittedAsync(changes, Guid.NewGuid(), lease, CancellationToken.None);
        }
        public async Task<SettingsAuthorityResult> ApplyAsync(SettingKey key, CapturingParticipant? wrapper = null)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications,
                item => item.Entries.Any(entry => entry.Key == key));
            return await Lifetime.Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, (ISettingsApplicationParticipant?)wrapper ?? Participant,
                SettingsApplicationPhase.Live, lease, CancellationToken.None);
        }
        public Task<SettingsAuthorityResult> RetryAsync(SettingsAuthorityResult failed)
        {
            SettingsApplicationBatch batch = Assert.Single(failed.Envelope!.PendingApplications);
            return Authority.RetryAsync(batch.BatchId, batch.AttemptId, Guid.NewGuid(), CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            await Generations.DisposeAsync();
            if (Lifetime is not null) { await Lifetime.DisposeAsync(); }
            Native.Dispose();
            await Directory.DisposeAsync();
        }
    }

    private sealed class Lifetime : IServiceProvider, IAsyncDisposable
    {
        private readonly SettingsGenerationContext _context;
        private Lifetime(DataGenerationDescriptor generation, MutationAdmissionBarrier admission, NativeSurface native)
        {
            Repository = new(generation, SettingsRegistry.Default);
            Session = new(Repository, SettingsRegistry.Default, admission);
            Dispatcher = native.CreateDispatcher();
            Participant = new(generation, admission, SettingsRegistry.Default, Dispatcher, native);
            _context = new(Session, [Participant]);
        }
        public JsonSettingsRepository Repository { get; }
        public SettingsAuthoritySession Session { get; }
        public OwnedUiDispatcher Dispatcher { get; }
        public AppearanceSettingsParticipant Participant { get; }
        public static async Task<Lifetime> CreateAsync(DataGenerationDescriptor generation, MutationAdmissionBarrier admission, NativeSurface native)
        {
            Lifetime lifetime = new(generation, admission, native);
            try
            {
                Assert.True((await lifetime.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                return lifetime;
            }
            catch { await lifetime.DisposeAsync(); throw; }
        }
        public object? GetService(Type serviceType) => serviceType == typeof(IAppearanceSettingsReader) ? Participant
            : serviceType == typeof(SettingsGenerationContext) ? _context : null;
        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); await Participant.DisposeAsync(); }
    }

    private sealed class CapturingParticipant(AppearanceSettingsParticipant inner) : ISettingsApplicationParticipant
    {
        public SettingApplicationKind ApplicationKind => SettingApplicationKind.Appearance;
        public SettingsApplicationRequest? Request { get; private set; }
        public Func<Task>? BeforeApply { get; init; }
        public bool LoseReply { get; init; }
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        { Request = request; return inner.ProbeAsync(request, lease, cancellationToken); }
        public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        {
            if (BeforeApply is not null) { await BeforeApply(); }
            await inner.ApplyAsync(request, lease, cancellationToken);
            if (LoseReply) { throw new IOException("Isolated lost participant reply."); }
        }
    }

    private sealed class NativeSurface : IAppearanceNativeSettings, IDisposable
    {
        private readonly AsyncLocal<bool> _insideUi = new();
        private readonly CancellationTokenSource _window = new();
        private readonly ConcurrentQueue<Action> _queued = new();
        private readonly AccentColorRuntime _accent;
        private AppLanguage _language = AppLanguage.AutoDetect;
        private AppThemeMode _theme = AppThemeMode.FollowSystem;
        public NativeSurface() { Resources = new(() => Assert.True(_insideUi.Value)); _accent = AppThemeService.CreateAccentRuntime(Resources); }
        public Resources Resources { get; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int ResourceReads => Resources.Reads;
        public bool Unavailable { get; set; }
        public bool FailNextTheme { get; set; }
        public bool LoseNextThemeReply { get; set; }
        public bool IgnoreThemeWrites { get; set; }
        public Exception? ThemeFailure { get; set; }
        public bool HoldQueue { get; set; }
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OwnedUiDispatcher CreateDispatcher() => new(() => _insideUi.Value, action =>
        {
            if (HoldQueue) { _queued.Enqueue(action); Queued.TrySetResult(); return true; }
            RunOnUi(action);
            return true;
        }, _window.Token);
        public void DrainQueue() { while (_queued.TryDequeue(out Action? action)) { RunOnUi(action); } }
        private void RunOnUi(Action action)
        {
            bool previous = _insideUi.Value;
            _insideUi.Value = true;
            try { action(); }
            finally { _insideUi.Value = previous; }
        }
        public AppearanceNativeConfiguration CaptureConfiguration()
        {
            Assert.True(_insideUi.Value);
            ++Reads;
            if (Unavailable) { throw new IOException("Isolated unavailable window."); }
            return new(_language, _theme, _accent.CaptureConfiguration());
        }
        public void ApplyLanguage(AppLanguage language) { Assert.True(_insideUi.Value); ++Writes; _language = language; }
        public void ApplyAccent(AccentColorConfiguration accent) { Assert.True(_insideUi.Value); ++Writes; _accent.Apply(accent); }
        public void ApplyTheme(AppThemeMode theme)
        {
            Assert.True(_insideUi.Value);
            ++Writes;
            if (ThemeFailure is not null) { throw ThemeFailure; }
            if (FailNextTheme) { FailNextTheme = false; throw new IOException("Isolated theme write failure."); }
            if (!IgnoreThemeWrites) { _theme = theme; }
            if (LoseNextThemeReply) { LoseNextThemeReply = false; throw new IOException("Isolated lost native reply."); }
        }
        public void Dispose() => _window.Dispose();
    }

    private sealed class Resources(Action assertUi) : IAccentResourceStore
    {
        public Dictionary<string, AccentResourceValue> Values { get; } = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public int FailAfterWrite { get; set; }
        public bool FailRemovals { get; set; }
        public IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys)
        {
            assertUi(); ++Reads;
            return Values.Where(pair => ownedKeys.Contains(pair.Key, StringComparer.Ordinal)).ToDictionary(pair => pair.Key, pair => (AccentResourceValue?)pair.Value, StringComparer.Ordinal);
        }
        public void WriteOverride(string key, AccentResourceValue value)
        {
            assertUi(); Values[key] = value;
            if (FailAfterWrite > 0 && --FailAfterWrite == 0) { throw new IOException("Isolated partial palette write."); }
        }
        public void RemoveOverride(string key)
        {
            assertUi();
            if (FailRemovals) { throw new IOException("Isolated unavailable palette removal."); }
            Values.Remove(key);
        }
    }
}
