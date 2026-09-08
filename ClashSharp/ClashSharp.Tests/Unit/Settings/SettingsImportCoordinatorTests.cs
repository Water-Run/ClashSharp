using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.Settings;

public sealed class SettingsImportCoordinatorTests
{
    private const string PackagePath = "selected-backup.clashsharp";
    private static readonly string[] Participants = ["language", "theme", "accent", "startup", "sampling", "network"];
    private static readonly SettingsRuntimeSnapshot Baseline = new(
        AppLanguage.English, AppThemeMode.Dark, AppAccentColorMode.Custom, "#FF123456",
        true, false, 30, ClashSharpMode.RuleTakeover, "old-profile", false, 7890);
    private static readonly SettingsRuntimeSnapshot Imported = new(
        AppLanguage.French, AppThemeMode.Light, AppAccentColorMode.FollowSystem, "#FF0078D4",
        false, true, 10, ClashSharpMode.Disabled, "imported-profile", true, 10000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_UsesTheRetainedGenerationAndCompletesAfterCallerCancellation(bool cancelAfterBegin)
    {
        using CancellationTokenSource cancellation = new();
        ProbeOperation operation = new() { OnBegin = cancelAfterBegin ? cancellation.Cancel : null };
        await ExecuteAsync(operation, cancellation.Token);

        Assert.Equal(Imported, operation.Current);
        Assert.Equal(Participants, operation.Applied.Select(static call => call.Participant));
        Assert.All(operation.Applied, call => Assert.Equal(Imported, call.Snapshot));
        Assert.Equal(1, operation.Invalidations);
        Assert.Equal(1, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.True(operation.Disposed);
        Assert.Equal(["read", "begin", "profiles", "read", .. Participants, "read", "commit", "dispose"], operation.Events);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("theme")]
    [InlineData("accent")]
    [InlineData("startup")]
    [InlineData("sampling")]
    [InlineData("network")]
    public async Task ActivationFailure_RestoresTheCompleteBaselineAndAttemptsAllParticipants(string participant)
    {
        IOException failure = new("Imported participant failed.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name == participant && snapshot == Imported ? failure : null,
        };
        Exception actual = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(operation));

        Assert.Same(failure, actual);
        Assert.Equal(Participants.Concat(Participants), operation.Applied.Select(static call => call.Participant));
        Assert.All(operation.Applied.Take(6), call => Assert.Equal(Imported, call.Snapshot));
        Assert.All(operation.Applied.Skip(6), call => Assert.Equal(Baseline, call.Snapshot));
        Assert.Equal(Baseline, operation.Current);
        Assert.Equal(2, operation.Invalidations);
        Assert.Equal(0, operation.CommitCalls);
        Assert.Equal(1, operation.RollbackCalls);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("theme")]
    [InlineData("accent")]
    [InlineData("startup")]
    [InlineData("sampling")]
    [InlineData("network")]
    public async Task CompensationFailure_RetainsBothDiagnosticsAndAttemptsRemainingParticipants(string participant)
    {
        IOException activationFailure = new("Imported participant failed.");
        IOException compensationFailure = new("Previous participant could not be restored.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name != participant ? null
                : snapshot == Imported ? activationFailure : compensationFailure,
        };
        SettingsImportRecoveryException actual = await Assert.ThrowsAsync<SettingsImportRecoveryException>(() => ExecuteAsync(operation));

        Assert.Same(activationFailure, actual.ActivationFailure);
        Assert.Same(compensationFailure, actual.RecoveryFailure);
        Assert.Equal(Participants.Concat(Participants), operation.Applied.Select(static call => call.Participant));
        Assert.Equal(1, operation.RollbackCalls);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileInvalidationFailure_IsCoveredByRetainedRollback(bool compensationFails)
    {
        IOException activationFailure = new("Imported profile invalidation failed.");
        IOException compensationFailure = new("Previous profile invalidation failed.");
        ProbeOperation operation = new()
        {
            OnInvalidate = call => call == 1 ? activationFailure : compensationFails ? compensationFailure : null,
        };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(2, operation.Invalidations);
        Assert.Equal(1, operation.RollbackCalls);
        Assert.Equal(Baseline, operation.Current);
        if (compensationFails)
        {
            SettingsImportRecoveryException recovery = Assert.IsType<SettingsImportRecoveryException>(actual);
            Assert.Same(activationFailure, recovery.ActivationFailure);
            Assert.Same(compensationFailure, recovery.RecoveryFailure);
            Assert.Empty(operation.Applied);
        }
        else
        {
            Assert.Same(activationFailure, actual);
            Assert.Equal(Participants, operation.Applied.Select(static call => call.Participant));
            Assert.All(operation.Applied, call => Assert.Equal(Baseline, call.Snapshot));
        }

        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitCleanupFailure_RetriesOnlyTheCommitDecision(bool retryFails)
    {
        IOException failure = new("Commit cleanup unavailable.");
        ProbeOperation operation = new() { OnCommit = call => call == 1 || retryFails ? failure : null };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(2, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.Equal(1, operation.Invalidations);
        Assert.Equal(6, operation.Applied.Count);
        Assert.Equal(Imported, operation.Current);
        if (retryFails)
        {
            Assert.Equal([failure, failure], Assert.IsType<AggregateException>(actual).InnerExceptions);
        }
        else
        {
            Assert.Null(actual);
        }

        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackCleanupFailure_IsRetriedBeforeProfileAndRuntimeCompensation(bool retryFails)
    {
        IOException activationFailure = new("Imported startup failed.");
        IOException rollbackFailure = new("Rollback unavailable.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name == "startup" && snapshot == Imported ? activationFailure : null,
            OnRollback = call => call == 1 || retryFails ? rollbackFailure : null,
        };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(2, operation.RollbackCalls);
        Assert.Equal(0, operation.CommitCalls);
        Assert.Equal(retryFails ? 1 : 2, operation.Invalidations);
        Assert.Equal(retryFails ? 6 : 12, operation.Applied.Count);
        if (retryFails)
        {
            SettingsImportRecoveryException recovery = Assert.IsType<SettingsImportRecoveryException>(actual);
            Assert.Same(activationFailure, recovery.ActivationFailure);
            Assert.Equal([rollbackFailure, rollbackFailure], Assert.IsType<AggregateException>(recovery.RecoveryFailure).InnerExceptions);
        }
        else
        {
            Assert.Same(activationFailure, actual);
            Assert.Equal(Baseline, operation.Current);
        }

        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task IncompleteDurableRollback_DoesNotApplyTheOldRuntimeAgainstUnrestoredSettings()
    {
        IOException failure = new("Imported startup failed.");
        ProbeOperation operation = new()
        {
            SkipRollbackRestore = true,
            OnApply = (name, _) => name == "startup" ? failure : null,
        };
        SettingsImportRecoveryException actual = await Assert.ThrowsAsync<SettingsImportRecoveryException>(() => ExecuteAsync(operation));

        Assert.Same(failure, actual.ActivationFailure);
        Assert.Contains("did not restore", actual.RecoveryFailure.Message, StringComparison.Ordinal);
        Assert.Equal(6, operation.Applied.Count);
        Assert.All(operation.Applied, call => Assert.Equal(Imported, call.Snapshot));
        Assert.Equal(Imported, operation.Current);
        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task ParticipantChangesDurableAuthority_VerificationRollsBackTheImport()
    {
        ProbeOperation operation = new();
        operation.OnApply = (name, snapshot) =>
        {
            if (name == "network" && snapshot == Imported)
            {
                operation.Current = Imported with { MixedPort = 4567 };
            }

            return null;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(operation));

        Assert.Equal(1, operation.RollbackCalls);
        Assert.Equal(Baseline, operation.Current);
        Assert.All(operation.Applied.Skip(6), call => Assert.Equal(Baseline, call.Snapshot));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AuthorityReadFailure_IsAnActivationOrRecoveryFailure(int failedRead)
    {
        IOException readFailure = new("Authority unavailable.");
        IOException activationFailure = new("Imported startup failed.");
        ProbeOperation operation = new()
        {
            OnRead = read => read == failedRead ? readFailure : null,
            OnApply = (name, snapshot) => failedRead == 4 && name == "startup" && snapshot == Imported ? activationFailure : null,
        };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(1, operation.RollbackCalls);
        Assert.Equal(0, operation.CommitCalls);
        if (failedRead == 4)
        {
            SettingsImportRecoveryException recovery = Assert.IsType<SettingsImportRecoveryException>(actual);
            Assert.Same(activationFailure, recovery.ActivationFailure);
            Assert.Same(readFailure, recovery.RecoveryFailure);
            Assert.Equal(6, operation.Applied.Count);
        }
        else
        {
            Assert.Same(readFailure, actual);
            Assert.All(operation.Applied.TakeLast(6), call => Assert.Equal(Baseline, call.Snapshot));
        }

        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeBegin_DoesNotImportOrActivate(bool cancelDuringRead)
    {
        using CancellationTokenSource cancellation = new();
        ProbeOperation operation = new()
        {
            OnRead = _ => { if (cancelDuringRead) { cancellation.Cancel(); } return null; },
        };
        if (!cancelDuringRead)
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(operation, cancellation.Token));
        Assert.DoesNotContain("begin", operation.Events);
        Assert.Equal(Baseline, operation.Current);
        Assert.Empty(operation.Applied);
        Assert.False(operation.Disposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidPackagePath_IsRejectedBeforeStorageAccess(string? path)
    {
        ProbeOperation operation = new();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new SettingsImportCoordinator().ExecuteAsync(operation, path!, CancellationToken.None));
        Assert.Empty(operation.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBegin_DoesNotInventAReceiptOrApplyRuntime(bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException() : new IOException("Import could not begin.");
        ProbeOperation operation = new() { BeginFailure = failure };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Same(failure, actual);
        Assert.Equal(Baseline, operation.Current);
        Assert.Equal(0, operation.Invalidations);
        Assert.Equal(0, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.False(operation.Disposed);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("compensated")]
    [InlineData("recovery")]
    public async Task ReceiptDisposalFailure_PreservesTheDurableDecisionAndRecoveryClassification(string stage)
    {
        IOException activationFailure = new("Imported startup failed.");
        IOException compensationFailure = new("Previous startup could not recover.");
        IOException disposalFailure = new("Receipt disposal failed.");
        ProbeOperation operation = new()
        {
            DisposeFailure = disposalFailure,
            OnApply = (name, snapshot) => stage == "success" || name != "startup" ? null
                : snapshot == Imported ? activationFailure : stage == "recovery" ? compensationFailure : null,
        };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.True(operation.Disposed);
        Assert.Equal(stage == "success" ? 1 : 0, operation.CommitCalls);
        Assert.Equal(stage == "success" ? 0 : 1, operation.RollbackCalls);
        if (stage == "success")
        {
            Assert.Same(disposalFailure, actual);
        }
        else if (stage == "compensated")
        {
            Assert.Equal([activationFailure, disposalFailure], Assert.IsType<AggregateException>(actual).InnerExceptions);
        }
        else
        {
            SettingsImportRecoveryException recovery = Assert.IsType<SettingsImportRecoveryException>(actual);
            Assert.Same(activationFailure, recovery.ActivationFailure);
            Assert.Equal([compensationFailure, disposalFailure], Assert.IsType<AggregateException>(recovery.RecoveryFailure).InnerExceptions);
        }
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("apply")]
    [InlineData("commit")]
    public async Task FatalExceptionGraph_DoesNotTriggerRetryOrCompensation(string stage)
    {
        AggregateException failure = new(Activator.CreateInstance<OutOfMemoryException>());
        ProbeOperation operation = new()
        {
            BeginFailure = stage == "begin" ? failure : null,
            OnApply = (name, _) => stage == "apply" && name == "language" ? failure : null,
            OnCommit = _ => stage == "commit" ? failure : null,
        };
        Exception actual = await Assert.ThrowsAsync<AggregateException>(() => ExecuteAsync(operation));

        Assert.Same(failure, actual);
        Assert.True(ExceptionGraphClassifier.IsProcessFatal(actual));
        Assert.Equal(0, operation.RollbackCalls);
        Assert.Equal(stage == "commit" ? 1 : 0, operation.CommitCalls);
        Assert.Equal(stage != "begin", operation.Disposed);
    }

    private static Task ExecuteAsync(ProbeOperation operation, CancellationToken cancellationToken = default) =>
        new SettingsImportCoordinator().ExecuteAsync(operation, PackagePath, cancellationToken);

    private sealed class ProbeOperation : ISettingsImportOperation, IRetainedSettingsTransactionReceipt
    {
        public SettingsRuntimeSnapshot Current { get; set; } = Baseline;
        public List<string> Events { get; } = [];
        public List<(string Participant, SettingsRuntimeSnapshot Snapshot)> Applied { get; } = [];
        public Action? OnBegin { get; init; }
        public Func<int, Exception?>? OnRead { get; init; }
        public Func<int, Exception?>? OnInvalidate { get; init; }
        public Func<string, SettingsRuntimeSnapshot, Exception?>? OnApply { get; set; }
        public Func<int, Exception?>? OnCommit { get; init; }
        public Func<int, Exception?>? OnRollback { get; init; }
        public Exception? BeginFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public bool SkipRollbackRestore { get; init; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public int Invalidations { get; private set; }
        public bool Disposed { get; private set; }
        private int ReadCalls { get; set; }

        public SettingsRuntimeSnapshot CaptureSnapshot()
        {
            Events.Add("read");
            ReadCalls++;
            Exception? failure = OnRead?.Invoke(ReadCalls);
            if (failure is not null) { throw failure; }
            return Current;
        }

        public async Task<IRetainedSettingsTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken)
        {
            Assert.Equal(PackagePath, packagePath);
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("begin");
            await Task.Yield();
            if (BeginFailure is not null) { throw BeginFailure; }
            Current = Imported;
            OnBegin?.Invoke();
            return this;
        }

        public void InvalidateProfiles()
        {
            Events.Add("profiles");
            Invalidations++;
            Exception? failure = OnInvalidate?.Invoke(Invalidations);
            if (failure is not null) { throw failure; }
        }

        public void ApplyLanguage(AppLanguage language) { Assert.Equal(Current.DisplayLanguage, language); Apply("language"); }
        public void ApplyTheme(AppThemeMode theme) { Assert.Equal(Current.AppThemeMode, theme); Apply("theme"); }
        public void ApplyAccentColor(AppAccentColorMode mode, string value)
        {
            Assert.Equal(Current.AppAccentColorMode, mode);
            Assert.Equal(Current.AppAccentColorValue, value);
            Apply("accent");
        }

        public Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.LaunchAtStartupEnabled, enabled);
            return ApplyAsync("startup", cancellationToken);
        }

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken) => ApplyAsync("sampling", cancellationToken);
        public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.TransparentProxyEnabled, transparentProxyEnabled);
            Assert.Equal(Current.MixedPort, mixedPort);
            return ApplyAsync("network", cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Events.Add("commit");
            CommitCalls++;
            Exception? failure = OnCommit?.Invoke(CommitCalls);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Events.Add("rollback");
            RollbackCalls++;
            Exception? failure = OnRollback?.Invoke(RollbackCalls);
            if (failure is not null) { return Task.FromException(failure); }
            if (!SkipRollbackRestore) { Current = Baseline; }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            Disposed = true;
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }

        private void Apply(string participant)
        {
            Events.Add(participant);
            Applied.Add((participant, Current));
            Exception? failure = OnApply?.Invoke(participant, Current);
            if (failure is not null) { throw failure; }
        }

        private async Task ApplyAsync(string participant, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            await Task.Yield();
            Apply(participant);
        }
    }
}
