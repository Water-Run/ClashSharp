using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Settings;

public sealed class SettingsResetCoordinatorTests
{
    private static readonly string[] AllParticipants = ["language", "theme", "accent", "startup", "sampling", "network"];
    private static readonly SettingsRuntimeSnapshot Baseline = new(
        AppLanguage.English, AppThemeMode.Dark, AppAccentColorMode.Custom, "#FF123456",
        true, false, 30, ClashSharpMode.RuleTakeover, "baseline-profile", false, 7890);
    private static readonly SettingsRuntimeSnapshot Defaults = new(
        AppLanguage.AutoDetect, AppThemeMode.FollowSystem, AppAccentColorMode.FollowSystem, "#FF0078D4",
        false, true, 10, ClashSharpMode.Disabled, string.Empty, true, 10000);

    [Theory]
    [InlineData(SettingsResetScope.All, "language,theme,accent,startup,sampling,network")]
    [InlineData(SettingsResetScope.Startup, "startup")]
    [InlineData(SettingsResetScope.Proxy, "sampling,network")]
    [InlineData(SettingsResetScope.TransparentProxy, "network")]
    public async Task SuccessfulReset_SelectsOnlyOwnedParticipantsAndPublishesBeforeCleanup(SettingsResetScope scope, string participants)
    {
        ProbeOperation operation = new();
        await ExecuteAsync(operation, scope);

        Assert.Equal(participants.Split(','), operation.Applied.Select(static value => value.Participant));
        Assert.All(operation.Applied, call => Assert.Equal(Defaults, call.Snapshot));
        Assert.Equal((Defaults, scope, false), Assert.Single(operation.Published));
        Assert.Equal(["read", "begin", "read", .. participants.Split(','), "read", "publish", "commit", "dispose"], operation.Events);
        Assert.Equal(1, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(SettingsResetScope.None)]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.Proxy | SettingsResetScope.Startup)]
    public async Task InvalidScope_IsRejectedBeforeReadingStorage(SettingsResetScope scope)
    {
        ProbeOperation operation = new();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ExecuteAsync(operation, scope));
        Assert.Empty(operation.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeDurableWork_DoesNotBeginOrApply(bool cancelDuringRead)
    {
        using CancellationTokenSource cancellation = new();
        ProbeOperation operation = new() { OnRead = cancelDuringRead ? cancellation.Cancel : null };
        if (!cancelDuringRead)
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExecuteAsync(operation, cancellationToken: cancellation.Token));

        Assert.Equal(cancelDuringRead ? ["read"] : Array.Empty<string>(), operation.Events);
        Assert.Equal(Baseline, operation.Current);
        Assert.Empty(operation.Applied);
        Assert.False(operation.Disposed);
    }

    [Fact]
    public async Task CancellationAfterBegin_CompletesEveryParticipantAndTheRetainedDecision()
    {
        using CancellationTokenSource cancellation = new();
        ProbeOperation operation = new() { OnBegin = cancellation.Cancel };
        await ExecuteAsync(operation, cancellationToken: cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(AllParticipants, operation.Applied.Select(static call => call.Participant));
        Assert.Equal(1, operation.CommitCalls);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("theme")]
    [InlineData("accent")]
    [InlineData("startup")]
    [InlineData("sampling")]
    [InlineData("network")]
    public async Task FailedActivation_AttemptsAllParticipantsAndRestoresTheRetainedBaseline(string participant)
    {
        IOException failure = new("Activation failed.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name == participant && snapshot == Defaults ? failure : null,
        };

        Exception actual = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(operation));

        Assert.Same(failure, actual);
        Assert.Equal(AllParticipants.Concat(AllParticipants), operation.Applied.Select(static call => call.Participant));
        Assert.All(operation.Applied.Take(6), call => Assert.Equal(Defaults, call.Snapshot));
        Assert.All(operation.Applied.Skip(6), call => Assert.Equal(Baseline, call.Snapshot));
        Assert.Equal((Baseline, SettingsResetScope.All, true), Assert.Single(operation.Published));
        Assert.Equal(1, operation.RollbackCalls);
        Assert.Equal(0, operation.CommitCalls);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData("language")]
    [InlineData("theme")]
    [InlineData("accent")]
    [InlineData("startup")]
    [InlineData("sampling")]
    [InlineData("network")]
    public async Task FailedCompensation_ReportsBothFailuresAndNeverPublishesSuccess(string participant)
    {
        IOException activationFailure = new("Activation failed.");
        IOException recoveryFailure = new("Compensation failed.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name != participant ? null
                : snapshot == Defaults ? activationFailure : recoveryFailure,
        };

        SettingsResetRecoveryException actual = await Assert.ThrowsAsync<SettingsResetRecoveryException>(() => ExecuteAsync(operation));

        Assert.Same(activationFailure, actual.ActivationFailure);
        Assert.Same(recoveryFailure, actual.RecoveryFailure);
        Assert.Equal(AllParticipants.Concat(AllParticipants), operation.Applied.Select(static call => call.Participant));
        Assert.Empty(operation.Published);
        Assert.Equal(Baseline, operation.Current);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialBeginFailure_ConvergesToReadableAuthorityWithoutInventingAReceipt(bool convergenceFails)
    {
        IOException resetFailure = new("Reset interrupted after changing durable values.");
        IOException runtimeFailure = new("Runtime could not converge.");
        ProbeOperation operation = new()
        {
            BeginFailure = resetFailure,
            OnApply = (name, _) => convergenceFails && name == "startup" ? runtimeFailure : null,
        };

        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(AllParticipants, operation.Applied.Select(static call => call.Participant));
        Assert.All(operation.Applied, call => Assert.Equal(Defaults, call.Snapshot));
        Assert.Equal(0, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.False(operation.Disposed);
        if (convergenceFails)
        {
            SettingsResetRecoveryException recovery = Assert.IsType<SettingsResetRecoveryException>(actual);
            Assert.Same(resetFailure, recovery.ActivationFailure);
            Assert.Same(runtimeFailure, recovery.RecoveryFailure);
            Assert.Empty(operation.Published);
        }
        else
        {
            Assert.Same(resetFailure, actual);
            Assert.Equal((Defaults, SettingsResetScope.All, true), Assert.Single(operation.Published));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailure_RetriesTheSameDecisionAndPreservesActivatedState(bool retryFails)
    {
        IOException failure = new("Commit cleanup unavailable.");
        ProbeOperation operation = new() { OnCommit = call => call == 1 || retryFails ? failure : null };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        if (retryFails)
        {
            Assert.All(Assert.IsType<AggregateException>(actual).InnerExceptions, error => Assert.Same(failure, error));
        }
        else
        {
            Assert.Null(actual);
        }

        Assert.Equal(2, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.Equal(Defaults, operation.Current);
        Assert.Equal((Defaults, SettingsResetScope.All, false), Assert.Single(operation.Published));
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackFailure_RetriesBeforeCompensatingAndDoesNotClaimAnUnrestoredBaseline(bool retryFails)
    {
        IOException activationFailure = new("Activation failed.");
        IOException rollbackFailure = new("Rollback unavailable.");
        ProbeOperation operation = new()
        {
            OnApply = (name, snapshot) => name == "startup" && snapshot == Defaults ? activationFailure : null,
            OnRollback = call => call == 1 || retryFails ? rollbackFailure : null,
        };
        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation, SettingsResetScope.Startup));

        Assert.Equal(2, operation.RollbackCalls);
        Assert.Equal(0, operation.CommitCalls);
        if (retryFails)
        {
            SettingsResetRecoveryException recovery = Assert.IsType<SettingsResetRecoveryException>(actual);
            Assert.Same(activationFailure, recovery.ActivationFailure);
            Assert.Equal(2, Assert.IsType<AggregateException>(recovery.RecoveryFailure).InnerExceptions.Count);
            Assert.Single(operation.Applied);
            Assert.Empty(operation.Published);
            Assert.Equal(Defaults, operation.Current);
        }
        else
        {
            Assert.Same(activationFailure, actual);
            Assert.Equal([Defaults, Baseline], operation.Applied.Select(static call => call.Snapshot));
            Assert.Equal((Baseline, SettingsResetScope.Startup, true), Assert.Single(operation.Published));
        }

        Assert.True(operation.Disposed);
    }

    [Fact]
    public async Task ParticipantMutatesAuthority_VerificationForcesRollbackBeforePublishing()
    {
        ProbeOperation operation = new();
        operation.OnApply = (name, snapshot) =>
        {
            if (name == "network" && snapshot == Defaults)
            {
                operation.Current = Defaults with { MixedPort = 4567 };
            }

            return null;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(operation, SettingsResetScope.TransparentProxy));
        Assert.Equal(1, operation.RollbackCalls);
        Assert.Equal((Baseline, SettingsResetScope.TransparentProxy, true), Assert.Single(operation.Published));
        Assert.Equal(Baseline, operation.Current);
    }

    [Fact]
    public async Task ReceiptClaimsRollbackWithoutRestoringStorage_ConvergesToActualValuesAndRequiresRecovery()
    {
        ProbeOperation operation = new()
        {
            SkipRollbackRestore = true,
            OnApply = (name, _) => name == "startup" ? new IOException("Activation failed.") : null,
        };

        SettingsResetRecoveryException actual = await Assert.ThrowsAsync<SettingsResetRecoveryException>(
            () => ExecuteAsync(operation, SettingsResetScope.Startup));

        Assert.Contains("did not restore", actual.RecoveryFailure.ToString(), StringComparison.Ordinal);
        Assert.All(operation.Applied, call => Assert.Equal(Defaults, call.Snapshot));
        Assert.Empty(operation.Published);
        Assert.DoesNotContain("restore", operation.Events);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("apply")]
    [InlineData("commit")]
    public async Task FatalExceptionGraph_IsPropagatedWithoutRetryOrCompensation(string stage)
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
        Assert.Equal(0, operation.RollbackCalls);
        Assert.Equal(stage == "commit" ? 1 : 0, operation.CommitCalls);
        Assert.Equal(stage != "begin", operation.Disposed);
        Assert.Equal(stage == "commit" ? 1 : 0, operation.Published.Count);
    }

    [Fact]
    public async Task ReceiptDisposalFailure_PreservesThePublishedCommitDecision()
    {
        IOException failure = new("Receipt disposal failed.");
        ProbeOperation operation = new() { DisposeFailure = failure };
        Exception actual = await Assert.ThrowsAsync<IOException>(() => ExecuteAsync(operation));

        Assert.Same(failure, actual);
        Assert.Equal((Defaults, SettingsResetScope.All, false), Assert.Single(operation.Published));
        Assert.Equal(1, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliedObserverFails_FinalizesCommitAndPreservesBothDiagnostics(bool cleanupFails)
    {
        IOException observationFailure = new("Applied-state observer failed.");
        IOException cleanupFailure = new("Commit cleanup unavailable.");
        ProbeOperation operation = new()
        {
            ReportFailure = observationFailure,
            OnCommit = _ => cleanupFails ? cleanupFailure : null,
        };

        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation));

        Assert.Equal(cleanupFails ? 2 : 1, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
        Assert.Equal(Defaults, operation.Current);
        Assert.True(operation.Disposed);
        if (cleanupFails)
        {
            IReadOnlyCollection<Exception> failures = Assert.IsType<AggregateException>(actual).Flatten().InnerExceptions;
            Assert.Contains(observationFailure, failures);
            Assert.Contains(cleanupFailure, failures);
        }
        else
        {
            Assert.Same(observationFailure, actual);
        }
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("partial")]
    [InlineData("compensation")]
    public async Task AuthorityReadFailure_RollsBackOrReportsRecoveryWithoutPublishingUnreadableState(string stage)
    {
        IOException readFailure = new("Durable settings unavailable.");
        IOException originalFailure = new("Reset or activation failed.");
        ProbeOperation operation = new()
        {
            BeginFailure = stage == "partial" ? originalFailure : null,
            OnReadFailure = read => read == (stage == "compensation" ? 4 : 2) ? readFailure : null,
            OnApply = (name, snapshot) => stage == "compensation" && name == "startup" && snapshot == Defaults
                ? originalFailure : null,
        };

        Exception? actual = await Record.ExceptionAsync(() => ExecuteAsync(operation, SettingsResetScope.Startup));

        Assert.Equal(0, operation.CommitCalls);
        if (stage == "defaults")
        {
            Assert.Same(readFailure, actual);
            Assert.Equal(1, operation.RollbackCalls);
            Assert.Equal((Baseline, SettingsResetScope.Startup, true), Assert.Single(operation.Published));
            Assert.Equal(Baseline, Assert.Single(operation.Applied).Snapshot);
        }
        else
        {
            SettingsResetRecoveryException recovery = Assert.IsType<SettingsResetRecoveryException>(actual);
            Assert.Same(originalFailure, recovery.ActivationFailure);
            Assert.Same(readFailure, recovery.RecoveryFailure);
            Assert.Empty(operation.Published);
            Assert.Equal(stage == "partial" ? 0 : 1, operation.RollbackCalls);
        }

        Assert.Equal(stage != "partial", operation.Disposed);
    }

    [Fact]
    public async Task ReceiptDisposalFailure_DoesNotEraseTheRequiredRecoveryClassification()
    {
        IOException activationFailure = new("Activation failed.");
        IOException compensationFailure = new("Compensation failed.");
        IOException disposalFailure = new("Receipt disposal failed.");
        ProbeOperation operation = new()
        {
            OnApply = (_, snapshot) => snapshot == Defaults ? activationFailure : compensationFailure,
            DisposeFailure = disposalFailure,
        };

        SettingsResetRecoveryException actual = await Assert.ThrowsAsync<SettingsResetRecoveryException>(
            () => ExecuteAsync(operation, SettingsResetScope.Startup));

        Assert.Same(activationFailure, actual.ActivationFailure);
        Assert.Equal([compensationFailure, disposalFailure], Assert.IsType<AggregateException>(actual.RecoveryFailure).InnerExceptions);
        Assert.Empty(operation.Published);
        Assert.True(operation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptDisposalFailure_PreservesTheOriginalFailureGraph(bool fatal)
    {
        Exception originalFailure = fatal ? Activator.CreateInstance<OutOfMemoryException>() : new IOException("Observer failed.");
        IOException disposalFailure = new("Receipt disposal failed.");
        ProbeOperation operation = new()
        {
            ReportFailure = originalFailure,
            DisposeFailure = disposalFailure,
        };

        AggregateException actual = await Assert.ThrowsAsync<AggregateException>(() => ExecuteAsync(operation));

        Assert.Equal([originalFailure, disposalFailure], actual.InnerExceptions);
        Assert.Equal(fatal, ExceptionGraphClassifier.IsProcessFatal(actual));
        Assert.Equal(fatal ? 0 : 1, operation.CommitCalls);
        Assert.Equal(0, operation.RollbackCalls);
    }

    private static Task ExecuteAsync(
        ProbeOperation operation,
        SettingsResetScope scope = SettingsResetScope.All,
        CancellationToken cancellationToken = default) =>
        new SettingsResetCoordinator().ExecuteAsync(operation, scope, true, cancellationToken);

    private sealed class ProbeOperation : ISettingsResetOperation, IRetainedSettingsResetReceipt
    {
        public SettingsRuntimeSnapshot Current { get; set; } = Baseline;
        public List<string> Events { get; } = [];
        public List<(string Participant, SettingsRuntimeSnapshot Snapshot)> Applied { get; } = [];
        public List<(SettingsRuntimeSnapshot Snapshot, SettingsResetScope Scope, bool Failed)> Published { get; } = [];
        public Action? OnRead { get; init; }
        public Func<int, Exception?>? OnReadFailure { get; init; }
        public Action? OnBegin { get; init; }
        public Exception? BeginFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Exception? ReportFailure { get; init; }
        public Func<string, SettingsRuntimeSnapshot, Exception?>? OnApply { get; set; }
        public Func<int, Exception?>? OnCommit { get; init; }
        public Func<int, Exception?>? OnRollback { get; init; }
        public bool SkipRollbackRestore { get; init; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public bool Disposed { get; private set; }
        private int ReadCalls { get; set; }

        public SettingsRuntimeSnapshot CaptureSnapshot()
        {
            Events.Add("read");
            ReadCalls++;
            Exception? failure = OnReadFailure?.Invoke(ReadCalls);
            if (failure is not null)
            {
                throw failure;
            }

            OnRead?.Invoke();
            return Current;
        }

        public IRetainedSettingsResetReceipt BeginReset(SettingsResetScope scope, bool transparentProxyEnabled)
        {
            Events.Add("begin");
            Assert.True(transparentProxyEnabled);
            Current = Defaults;
            OnBegin?.Invoke();
            if (BeginFailure is not null)
            {
                throw BeginFailure;
            }

            return this;
        }

        public void RestoreDurableSnapshot(SettingsRuntimeSnapshot snapshot)
        {
            Events.Add("restore");
            Current = snapshot;
        }

        public void ApplyLanguage(AppLanguage language) => Apply("language");
        public void ApplyTheme(AppThemeMode theme) => Apply("theme");
        public void ApplyAccentColor(AppAccentColorMode mode, string value) => Apply("accent");
        public Task ApplyLaunchAtStartupAsync(bool enabled, CancellationToken cancellationToken) => ApplyAsync("startup", cancellationToken);
        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken) => ApplyAsync("sampling", cancellationToken);
        public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken) => ApplyAsync("network", cancellationToken);

        public void ReportApplied(SettingsRuntimeSnapshot snapshot, SettingsResetScope scope, bool operationFailed)
        {
            Events.Add("publish");
            Assert.False(Disposed);
            Published.Add((snapshot, scope, operationFailed));
            if (ReportFailure is not null)
            {
                throw ReportFailure;
            }
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
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            if (!SkipRollbackRestore)
            {
                Current = Baseline;
            }

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
            if (failure is not null)
            {
                throw failure;
            }
        }

        private async Task ApplyAsync(string participant, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            await Task.Yield();
            Apply(participant);
        }
    }
}
