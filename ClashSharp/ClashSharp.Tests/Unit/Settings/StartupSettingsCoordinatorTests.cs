using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Tests.Unit.Settings;

/// <summary>Checks startup commit ordering, independent baselines, compensation, and admission ownership.</summary>
public sealed class StartupSettingsCoordinatorTests
{
    [Fact]
    public void Construction_DoesNotReadSettingsOrContactThePlatform()
    {
        Probe probe = new();
        _ = new StartupSettingsCoordinator(new MutationAdmissionBarrier(), probe);
        Assert.Empty(probe.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Apply_VerifiesRegistrationBeforePublishingPreference(bool preference, bool registration)
    {
        Probe probe = new() { Preference = preference, Registration = registration };
        probe.OnWrite = (_, enabled) =>
        {
            Assert.Equal(enabled, probe.Registration);
            Assert.Equal(2, probe.RegistrationReads);
            probe.Preference = enabled;
        };

        await Create(probe).ApplyAsync(!preference, CancellationToken.None);

        Assert.Equal(!preference, probe.Preference);
        Assert.Equal(!preference, probe.Registration);
        Assert.Equal(["preference.read", "registration.read", "registration.apply", "registration.read", "preference.write", "preference.read"], probe.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartialPlatformFailure_RestoresPreferenceAndObservedRegistrationSeparately(bool preference, bool registration)
    {
        IOException failure = new("partial platform change");
        Probe probe = new() { Preference = preference, Registration = registration };
        probe.OnApply = (call, _) => call == 1 ? Task.FromException(failure) : Task.CompletedTask;

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(!preference, CancellationToken.None)));

        Assert.Equal(preference, probe.Preference);
        Assert.Equal(registration, probe.Registration);
        Assert.Equal([!preference, registration], probe.Applied);
        Assert.Equal(preference, Assert.Single(probe.Written));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceCommitFailure_RestoresBothBaselinesEvenAfterPublication(bool writeBeforeFailure)
    {
        IOException failure = new("preference commit failure");
        Probe probe = new();
        probe.OnWrite = (call, enabled) =>
        {
            if (call != 1 || writeBeforeFailure)
            {
                probe.Preference = enabled;
            }

            if (call == 1)
            {
                throw failure;
            }
        };

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(true, CancellationToken.None)));

        Assert.False(probe.Preference);
        Assert.False(probe.Registration);
        Assert.Equal([true, false], probe.Applied);
        Assert.Equal([true, false], probe.Written);
    }

    [Fact]
    public async Task UnknownBaseline_RejectsBeforeAnyMutation()
    {
        Probe probe = new() { Registration = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(probe).ApplyAsync(true, CancellationToken.None));

        Assert.Empty(probe.Applied);
        Assert.Empty(probe.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineReadFailure_PropagatesWithoutCompensation(bool platform)
    {
        IOException failure = new("baseline unavailable");
        Probe probe = new();
        if (platform)
        {
            probe.OnReadRegistration = _ => Task.FromException<bool?>(failure);
        }
        else
        {
            probe.OnReadPreference = _ => throw failure;
        }

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(probe).ApplyAsync(true, CancellationToken.None)));

        Assert.Empty(probe.Applied);
        Assert.Empty(probe.Written);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task UnverifiedRegistration_DoesNotCommitTheTarget(bool? observed)
    {
        Probe probe = new();
        probe.OnReadRegistration = call => Task.FromResult(call == 2 ? observed : probe.Registration);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(probe).ApplyAsync(true, CancellationToken.None));

        Assert.False(probe.Preference);
        Assert.False(probe.Registration);
        Assert.False(Assert.Single(probe.Written));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceVerificationFailure_RestoresAndDoesNotClaimSuccess(bool throws)
    {
        Probe probe = new();
        probe.OnReadPreference = call => call == 2
            ? throws ? throw new IOException("preference observation failed") : false
            : probe.Preference;

        Assert.NotNull(await Record.ExceptionAsync(() => Create(probe).ApplyAsync(true, CancellationToken.None)));

        Assert.False(probe.Preference);
        Assert.False(probe.Registration);
        Assert.Equal([true, false], probe.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationRecoveryFailure_StillRestoresThePreference(bool verification)
    {
        IOException applicationFailure = new("commit failed");
        IOException recoveryFailure = new("platform recovery failed");
        Probe probe = new();
        probe.OnWrite = (call, enabled) =>
        {
            probe.Preference = enabled;
            if (call == 1)
            {
                throw applicationFailure;
            }
        };
        if (verification)
        {
            probe.OnReadRegistration = call => call == 3 ? Task.FromException<bool?>(recoveryFailure) : Task.FromResult(probe.Registration);
        }
        else
        {
            probe.OnApply = (call, _) => call == 2 ? Task.FromException(recoveryFailure) : Task.CompletedTask;
        }

        StartupSettingsRecoveryException result = await Assert.ThrowsAsync<StartupSettingsRecoveryException>(
            () => Create(probe).ApplyAsync(true, CancellationToken.None));

        Assert.Same(applicationFailure, result.ApplicationFailure);
        Assert.Same(recoveryFailure, result.RecoveryFailure);
        Assert.False(probe.Preference);
        Assert.Equal([true, false], probe.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferenceRecoveryFailure_RemainsClassifiedAndPreservesThePlatformBaseline(bool verification)
    {
        IOException applicationFailure = new("platform apply failed");
        IOException recoveryFailure = new("preference recovery failed");
        Probe probe = new();
        probe.OnApply = (call, _) => call == 1 ? Task.FromException(applicationFailure) : Task.CompletedTask;
        if (verification)
        {
            probe.OnReadPreference = call => call == 2 ? throw recoveryFailure : probe.Preference;
        }
        else
        {
            probe.OnWrite = (_, _) => throw recoveryFailure;
        }

        StartupSettingsRecoveryException result = await Assert.ThrowsAsync<StartupSettingsRecoveryException>(
            () => Create(probe).ApplyAsync(true, CancellationToken.None));

        Assert.Same(applicationFailure, result.ApplicationFailure);
        Assert.Same(recoveryFailure, result.RecoveryFailure);
        Assert.False(probe.Registration);
    }

    [Fact]
    public async Task BothCompensationsFail_PreservesAllThreeDiagnostics()
    {
        IOException applicationFailure = new("apply");
        IOException registrationFailure = new("registration recovery");
        IOException preferenceFailure = new("preference recovery");
        Probe probe = new()
        {
            OnApply = (call, _) => Task.FromException(call == 1 ? applicationFailure : registrationFailure),
            OnWrite = (_, _) => throw preferenceFailure,
        };

        StartupSettingsRecoveryException result = await Assert.ThrowsAsync<StartupSettingsRecoveryException>(
            () => Create(probe).ApplyAsync(true, CancellationToken.None));

        Assert.Equal([applicationFailure, registrationFailure, preferenceFailure], result.Flatten().InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeMutation_DoesNotApplyOrWrite(bool duringObservation)
    {
        using CancellationTokenSource cancellation = new();
        Probe probe = new();
        if (duringObservation)
        {
            probe.OnReadRegistration = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult<bool?>(false);
            };
        }
        else
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(probe).ApplyAsync(true, cancellation.Token));

        Assert.Empty(probe.Applied);
        Assert.Empty(probe.Written);
    }

    [Fact]
    public async Task CancellationAfterMutationBegins_RetainsAdmissionThroughVerifiedCommit()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { OnApply = (_, _) => release.Task };
        MutationAdmissionBarrier admission = new();
        Task apply = new StartupSettingsCoordinator(admission, probe).ApplyAsync(true, cancellation.Token);
        cancellation.Cancel();
        Task<MutationAdmissionLease> drain = admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
        try
        {
            Assert.False(apply.IsCompleted);
            Assert.False(drain.IsCompleted);
            Assert.False(probe.Preference);
        }
        finally
        {
            release.TrySetResult();
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await using MutationAdmissionLease exclusive = await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(probe.Preference);
        Assert.True(probe.Registration);
        Assert.All(probe.MutationTokens, token => Assert.False(token.CanBeCanceled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingRequest_CanBeCancelledOrRevokedWithoutReadingOrMutating(bool revoke)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { OnApply = (_, _) => release.Task };
        MutationAdmissionBarrier admission = new();
        StartupSettingsCoordinator coordinator = new(admission, probe);
        Task first = coordinator.ApplyAsync(true, CancellationToken.None);
        Task second = coordinator.ApplyAsync(false, cancellation.Token);
        Task<MutationAdmissionLease>? drain = null;
        try
        {
            Assert.False(second.IsCompleted);
            if (revoke)
            {
                drain = admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            }
            else
            {
                cancellation.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, probe.PreferenceReads);
            Assert.True(Assert.Single(probe.Applied));
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (drain is not null)
            {
                await using MutationAdmissionLease exclusive = await drain.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task OrdinaryAndAdmittedRequests_SerializeBaselineCaptureAndRetainCallerOwnership()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe probe = new() { OnApply = (call, _) => call == 1 ? release.Task : Task.CompletedTask };
        MutationAdmissionBarrier admission = new();
        StartupSettingsCoordinator coordinator = new(admission, probe);
        using MutationAdmissionLease caller = admission.AcquireOrdinary();
        Task first = coordinator.ApplyAsync(true, CancellationToken.None);
        Task second = coordinator.ApplyAdmittedAsync(false, caller, CancellationToken.None);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(1, probe.PreferenceReads);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }

        admission.EnsureActiveLease(caller);
        Assert.Equal([true, false], probe.Applied);
        Assert.Equal([true, false], probe.Written);
        Assert.False(probe.Preference);
        Assert.False(probe.Registration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAdmission_IsRejectedBeforeAnyReads(bool disposed)
    {
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease lease = (disposed ? admission : new MutationAdmissionBarrier()).AcquireOrdinary();
        if (disposed)
        {
            lease.Dispose();
        }

        Probe probe = new();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new StartupSettingsCoordinator(admission, probe).ApplyAdmittedAsync(true, lease, CancellationToken.None));
        Assert.Empty(probe.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FatalFailureGraph_IsNeverConvertedToAnOrdinaryRecoveryResult(int stage)
    {
        Exception fatal = new AggregateException(new IOException("outer"), Activator.CreateInstance<OutOfMemoryException>());
        Probe probe = new();
        if (stage == 0)
        {
            probe.OnApply = (_, _) => Task.FromException(fatal);
        }
        else if (stage == 1)
        {
            probe.OnApply = (call, _) => Task.FromException(call == 1 ? new IOException("apply") : fatal);
        }
        else
        {
            probe.OnWrite = (_, _) => throw fatal;
        }

        Exception? result = await Record.ExceptionAsync(() => Create(probe).ApplyAsync(true, CancellationToken.None));
        Assert.Same(fatal, result);
        Assert.True(ExceptionGraphClassifier.IsProcessFatal(result!));
    }

    private static StartupSettingsCoordinator Create(Probe probe) => new(new MutationAdmissionBarrier(), probe);

    private sealed class Probe : IStartupSettingsOperation
    {
        public bool Preference { get; set; }
        public bool? Registration { get; set; } = false;
        public int PreferenceReads { get; private set; }
        public int RegistrationReads { get; private set; }
        public List<string> Calls { get; } = [];
        public List<bool> Applied { get; } = [];
        public List<bool> Written { get; } = [];
        public List<CancellationToken> MutationTokens { get; } = [];
        public Func<int, bool>? OnReadPreference { get; set; }
        public Func<int, Task<bool?>>? OnReadRegistration { get; set; }
        public Func<int, bool, Task>? OnApply { get; set; }
        public Action<int, bool>? OnWrite { get; set; }

        public bool ReadPreference()
        {
            Calls.Add("preference.read");
            ++PreferenceReads;
            return OnReadPreference?.Invoke(PreferenceReads) ?? Preference;
        }

        public Task<bool?> ReadRegistrationAsync(CancellationToken cancellationToken)
        {
            Calls.Add("registration.read");
            ++RegistrationReads;
            cancellationToken.ThrowIfCancellationRequested();
            return OnReadRegistration?.Invoke(RegistrationReads) ?? Task.FromResult(Registration);
        }

        public Task ApplyRegistrationAsync(bool enabled, CancellationToken cancellationToken)
        {
            Calls.Add("registration.apply");
            Applied.Add(enabled);
            MutationTokens.Add(cancellationToken);
            Registration = enabled;
            return OnApply?.Invoke(Applied.Count, enabled) ?? Task.CompletedTask;
        }

        public void WritePreference(bool enabled, MutationAdmissionLease admissionLease)
        {
            Calls.Add("preference.write");
            Written.Add(enabled);
            if (OnWrite is not null)
            {
                OnWrite(Written.Count, enabled);
            }
            else
            {
                Preference = enabled;
            }
        }
    }
}
