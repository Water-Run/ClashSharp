using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Model.Triggers;
using ClashSharpMode = ClashSharp.Model.ClashSharpMode;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies epoch-safe exit ownership transfers from trigger work to the outer runner.</summary>
public sealed class TriggerExitHandoffTests
{
    private static readonly Guid CurrentEpoch =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PriorEpoch =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task HandOffAsync_CurrentEpoch_DurablyPublishesOneKeyedRequest()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);

        TriggerActionApplyResult first = await coordinator.HandOffAsync(action, CancellationToken.None);
        TriggerActionApplyResult duplicate = await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        TriggerLifecycleHandoff handoff = await ReadHandoffAsync(
            repository,
            execution.ExecutionId,
            action.ActionIndex);

        Assert.Equal(TriggerActionApplyStatus.HandedOff, first.Status);
        Assert.Equal(TriggerActionApplyStatus.HandedOff, duplicate.Status);
        Assert.Equal(TriggerLifecycleHandoffState.HandedOff, handoff.State);
        Assert.Equal(TriggerOutboxState.HandedOff, (await ReadActionsAsync(repository, execution.ExecutionId))[0].State);
        Assert.Equal(ApplicationLifetimeRequestKind.Exit, request.Kind);
        Assert.NotNull(request.Handoff);
        Assert.Equal(TriggerLifecycleHandoffIdentity.CreateKey(
            execution.ExecutionId,
            action.ActionIndex,
            CurrentEpoch), request.Handoff.IdempotencyKey);
    }

    [Fact]
    public async Task DuplicatePublication_AfterCoordinatorRecreation_UsesDurableReleaseState()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator firstCoordinator = CreateCoordinator(repository, requests);
        await firstCoordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest acceptedRequest = await requests.ReadAsync(CancellationToken.None);
        TriggerLifecycleHandoffCoordinator recreatedCoordinator = CreateCoordinator(repository, requests);
        Assert.Equal(
            TriggerActionApplyStatus.HandedOff,
            (await recreatedCoordinator.HandOffAsync(action, CancellationToken.None)).Status);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace));

        Task processing = runner.ProcessAsync(acceptedRequest, CancellationToken.None);
        await recreatedCoordinator.AcknowledgeReleasedExecutionAsync(
            execution,
            CancellationToken.None);
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            ["host-stop-enter", "host-stop-unwound", "host-dispose"],
            trace);
        Assert.Equal(
            TriggerLifecycleHandoffState.Succeeded,
            (await ReadHandoffAsync(repository, execution.ExecutionId, action.ActionIndex)).State);
    }

    [Fact]
    public async Task ExecuteAsync_CurrentEpoch_UsesAtomicLifecycleTransitionWithoutSecondOutboxWrite()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, _) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator handoff = CreateCoordinator(repository, requests);
        TriggerActionExecutor executor = new(
            repository,
            new ExitOnlyRuntime(handoff),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None));

        Assert.Equal(TriggerOutboxState.HandedOff, result.FinalState);
        Assert.Equal(
            TriggerLifecycleHandoffState.HandedOff,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
        Assert.True(requests.HasAcceptedRequest);
    }

    [Fact]
    public async Task HandOffAsync_RequestChannelInitiallyBusy_AutomaticallyPromotesDurableExitAfterActiveRequestReleases()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, _) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        ApplicationLifetimeRequest blockingRequest = ApplicationLifetimeRequest.Exit("main-window");
        Assert.True(requests.TryRequest(blockingRequest));
        TriggerLifecycleHandoffCoordinator handoff = CreateCoordinator(repository, requests);
        TriggerActionExecutor executor = new(
            repository,
            new ExitOnlyRuntime(handoff),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        TriggerActionResult firstResult = Assert.Single(
            await executor.ExecuteAsync(execution, lease, CancellationToken.None));

        Assert.Equal(TriggerOutboxState.HandedOff, firstResult.FinalState);
        TriggerOutboxAction handedOffAction =
            (await ReadActionsAsync(repository, execution.ExecutionId))[0];
        Assert.Equal(TriggerOutboxState.HandedOff, handedOffAction.State);
        Assert.Equal(
            TriggerActionApplyStatus.HandedOff,
            (await handoff.HandOffAsync(handedOffAction, CancellationToken.None)).Status);
        Assert.Same(blockingRequest, await requests.ReadAsync(CancellationToken.None));
        Assert.False(await requests.RetryFailedRequestAsync(
            blockingRequest,
            CancellationToken.None));

        ApplicationLifetimeRequest published = await requests.ReadAsync(CancellationToken.None);

        Assert.True(requests.HasAcceptedRequest);
        Assert.Equal(
            TriggerLifecycleHandoffIdentity.CreateKey(execution.ExecutionId, 0, CurrentEpoch),
            published.Handoff?.IdempotencyKey);
        Assert.True(requests.TryRequest(ApplicationLifetimeRequest.Exit(
            "trigger-duplicate",
            published.Handoff!)));
    }

    [Fact]
    public async Task Repository_RejectsCompletionShortcutWithoutDifferentRecoveryEpoch()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        TriggerPersistenceResult<TriggerLifecycleHandoff> inserted =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    action.ActionIndex,
                    CurrentEpoch,
                    null,
                    TriggerLifecycleHandoffState.HandedOff,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        Assert.True(inserted.IsSucceeded, inserted.Diagnostic?.Code);

        TriggerPersistenceResult<TriggerLifecycleHandoff> shortcut =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    action.ActionIndex,
                    CurrentEpoch,
                    TriggerLifecycleHandoffState.HandedOff,
                    TriggerLifecycleHandoffState.Succeeded,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);

        Assert.Equal(TriggerPersistenceStatus.Invalid, shortcut.Status);
        Assert.Equal(
            TriggerLifecycleHandoffState.HandedOff,
            (await ReadHandoffAsync(repository, execution.ExecutionId, action.ActionIndex)).State);

        Assert.Throws<ArgumentException>(() => new TriggerLifecycleHandoffTransition(
            execution.ExecutionId,
            action.ActionIndex,
            CurrentEpoch,
            TriggerLifecycleHandoffState.HandedOff,
            TriggerLifecycleHandoffState.Succeeded,
            DateTimeOffset.UnixEpoch,
            recoveryProcessEpoch: CurrentEpoch));
        TriggerPersistenceResult<TriggerLifecycleHandoff> recovered =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    action.ActionIndex,
                    CurrentEpoch,
                    TriggerLifecycleHandoffState.HandedOff,
                    TriggerLifecycleHandoffState.Succeeded,
                    DateTimeOffset.UnixEpoch,
                    recoveryProcessEpoch: PriorEpoch),
                CancellationToken.None);
        Assert.True(recovered.IsSucceeded, recovered.Diagnostic?.Code);
    }

    [Fact]
    public async Task ProcessAsync_WaitsForExplicitReleaseThenStopsRecordsSuccessAndDisposes()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace));

        Task processTask = runner.ProcessAsync(request, CancellationToken.None);
        await Task.Yield();
        Assert.Empty(trace);
        trace.Add("repository-pin-released");
        trace.Add("execution-gate-released");
        trace.Add("supervisor-lease-released");
        await coordinator.AcknowledgeReleasedExecutionAsync(
            execution,
            CancellationToken.None);
        await processTask;

        Assert.Equal(
            [
                "repository-pin-released",
                "execution-gate-released",
                "supervisor-lease-released",
                "host-stop-enter",
                "host-stop-unwound",
                "host-dispose",
            ],
            trace);
        TriggerLifecycleHandoff handoff = await ReadHandoffAsync(
            repository,
            execution.ExecutionId,
            action.ActionIndex);
        Assert.Equal(TriggerLifecycleHandoffState.Succeeded, handoff.State);
        Assert.Equal(TriggerOutboxState.Succeeded, (await ReadActionsAsync(repository, execution.ExecutionId))[0].State);
    }

    [Theory]
    [InlineData(LifetimeCallbackFaultPoint.WaitForRelease)]
    [InlineData(LifetimeCallbackFaultPoint.MarkShutdownStarted)]
    [InlineData(LifetimeCallbackFaultPoint.MarkShutdownSucceeded)]
    [InlineData(LifetimeCallbackFaultPoint.MarkShutdownFailed)]
    public async Task ProcessAsync_FailOnceProductionCallback_AutomaticallyRetriesToDurableTerminalState(
        LifetimeCallbackFaultPoint faultPoint)
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(
            repository,
            new FailOnceCallbackRequestSink(requests, faultPoint));
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await coordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, 0, CurrentEpoch),
            CancellationToken.None);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace)
        {
            StopException = faultPoint == LifetimeCallbackFaultPoint.MarkShutdownFailed
                ? new InvalidOperationException("shutdown failed")
                : null,
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => runner.ProcessAsync(request, CancellationToken.None));
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Unconfirmed,
            request.TerminalStatePersistence);
        Assert.True(await requests.RetryFailedRequestAsync(request, CancellationToken.None));
        Assert.Same(request, await requests.ReadAsync(CancellationToken.None));

        if (faultPoint == LifetimeCallbackFaultPoint.MarkShutdownFailed)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ProcessAsync(request, CancellationToken.None));
            Assert.False(await requests.RetryFailedRequestAsync(request, CancellationToken.None));
            Assert.True(runner.HasAttachedHost);
        }
        else
        {
            await runner.ProcessAsync(request, CancellationToken.None);
            Assert.False(runner.HasAttachedHost);
        }

        Assert.Equal(1, trace.Count(entry => entry == "host-stop-enter"));
        Assert.Equal(
            ApplicationLifetimeTerminalStatePersistence.Confirmed,
            request.TerminalStatePersistence);
        Assert.Equal(
            faultPoint == LifetimeCallbackFaultPoint.MarkShutdownFailed
                ? TriggerLifecycleHandoffState.Uncertain
                : TriggerLifecycleHandoffState.Succeeded,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
    }

    [Fact]
    public async Task ProcessAsync_ShutdownFails_RecordsFailureAndKeepsHostOwned()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await coordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, action.ActionIndex, CurrentEpoch),
            CancellationToken.None);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace)
        {
            StopException = new InvalidOperationException("shutdown failed"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ProcessAsync(request, CancellationToken.None));

        TriggerLifecycleHandoff handoff = await ReadHandoffAsync(
            repository,
            execution.ExecutionId,
            action.ActionIndex);
        Assert.Equal(TriggerLifecycleHandoffState.Uncertain, handoff.State);
        Assert.Equal("trigger.handoff.shutdown_unexpected", handoff.LastError);
        Assert.Equal(["host-stop-enter"], trace);
        Assert.True(runner.HasAttachedHost);
    }

    [Fact]
    public async Task ProbeAsync_PriorEpochHandoff_MarksSuccessWithoutPublishingExit()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, PriorEpoch);
        TriggerPersistenceResult<TriggerLifecycleHandoff> inserted =
            await repository.TransitionLifecycleHandoffAsync(
                new TriggerLifecycleHandoffTransition(
                    execution.ExecutionId,
                    action.ActionIndex,
                    PriorEpoch,
                    null,
                    TriggerLifecycleHandoffState.HandedOff,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        Assert.True(inserted.IsSucceeded, inserted.Diagnostic?.Code);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);

        TriggerActionProbeResult result = await coordinator.ProbeAsync(action, CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.Desired, result.Status);
        Assert.False(requests.HasAcceptedRequest);
        TriggerLifecycleHandoff handoff = await ReadHandoffAsync(
            repository,
            execution.ExecutionId,
            action.ActionIndex);
        Assert.Equal(TriggerLifecycleHandoffState.Succeeded, handoff.State);
        Assert.Equal(TriggerOutboxState.Succeeded, (await ReadActionsAsync(repository, execution.ExecutionId))[0].State);
    }

    [Theory]
    [InlineData((int)TriggerLifecycleHandoffState.Failed)]
    [InlineData((int)TriggerLifecycleHandoffState.Uncertain)]
    public async Task ProbeAsync_PriorEpochFailedOrUncertainExit_IsSatisfiedByProcessTermination(
        int stateValue)
    {
        TriggerLifecycleHandoffState terminalState = (TriggerLifecycleHandoffState)stateValue;
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, PriorEpoch);
        TriggerLifecycleHandoffState? expected = null;
        foreach (TriggerLifecycleHandoffState next in new[]
                 {
                     TriggerLifecycleHandoffState.HandedOff,
                     TriggerLifecycleHandoffState.ReleaseAcknowledged,
                     TriggerLifecycleHandoffState.ShutdownStarted,
                     terminalState,
                 })
        {
            TriggerPersistenceResult<TriggerLifecycleHandoff> transition =
                await repository.TransitionLifecycleHandoffAsync(
                    new TriggerLifecycleHandoffTransition(
                        execution.ExecutionId,
                        action.ActionIndex,
                        PriorEpoch,
                        expected,
                        next,
                        DateTimeOffset.UnixEpoch,
                        next == terminalState ? "prior failure" : null),
                    CancellationToken.None);
            Assert.True(transition.IsSucceeded, transition.Diagnostic?.Code);
            expected = next;
        }

        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);

        TriggerActionProbeResult result = await coordinator.ProbeAsync(action, CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.Desired, result.Status);
        Assert.False(requests.HasAcceptedRequest);
        Assert.Equal(
            TriggerLifecycleHandoffState.Succeeded,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
    }

    [Fact]
    public async Task ProbeAsync_PriorEpochWithoutHandoff_CompletesWithoutPublishingExit()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, _, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, PriorEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);

        TriggerActionProbeResult result = await coordinator.ProbeAsync(action, CancellationToken.None);

        Assert.Equal(TriggerActionProbeStatus.Desired, result.Status);
        Assert.False(requests.HasAcceptedRequest);
        TriggerPersistenceResult<TriggerLifecycleHandoff> handoff =
            await repository.ReadLifecycleHandoffAsync(
                action.ExecutionId,
                action.ActionIndex,
                CancellationToken.None);
        Assert.Equal(TriggerPersistenceStatus.NotFound, handoff.Status);
    }

    [Fact]
    public async Task ExecuteAsync_PriorEpochWithoutHandoff_SucceedsWithoutExitPublication()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, _) =
            await SeedRunningExitAsync(directory.DatabasePath, PriorEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator handoff = CreateCoordinator(repository, requests);
        TriggerActionExecutor executor = new(
            repository,
            new ExitOnlyRuntime(handoff),
            NullTriggerFiredNotificationSink.Instance);
        MutationAdmissionBarrier admission = new();
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(
            CancellationToken.None);

        TriggerActionResult result = Assert.Single(await executor.ExecuteAsync(
            execution,
            lease,
            CancellationToken.None));

        Assert.Equal(TriggerOutboxState.Succeeded, result.FinalState);
        Assert.False(requests.HasAcceptedRequest);
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeLifecycleHandoffCommit, TriggerOutboxState.Running, false)]
    [InlineData(TriggerPersistenceFaultPoint.AfterLifecycleHandoffCommit, TriggerOutboxState.HandedOff, true)]
    public async Task HandOffAsync_PublicationCommitCut_ReconcilesWithoutDuplicateRequest(
        TriggerPersistenceFaultPoint faultPoint,
        TriggerOutboxState interruptedState,
        bool firstCallCompletes)
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository setupRepository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        SqliteTriggerRepository faultedRepository = await OpenFaultedAsync(
            directory.DatabasePath,
            new NthFaultInjector(faultPoint));
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(faultedRepository, requests);

        if (firstCallCompletes)
        {
            Assert.Equal(
                TriggerActionApplyStatus.HandedOff,
                (await coordinator.HandOffAsync(action, CancellationToken.None)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.HandOffAsync(action, CancellationToken.None));
        }

        Assert.Equal(
            interruptedState,
            (await ReadActionsAsync(setupRepository, execution.ExecutionId))[0].State);
        Assert.Equal(firstCallCompletes, requests.HasAcceptedRequest);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        Assert.Equal(
            TriggerLifecycleHandoffIdentity.CreateKey(execution.ExecutionId, 0, CurrentEpoch),
            request.Handoff?.IdempotencyKey);
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeLifecycleHandoffCommit)]
    [InlineData(TriggerPersistenceFaultPoint.AfterLifecycleHandoffCommit)]
    public async Task AcknowledgeReleaseAsync_CommitCut_ReconcilesBeforeReleasingRunner(
        TriggerPersistenceFaultPoint faultPoint)
    {
        using TriggerExitDirectory directory = new();
        (_, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        SqliteTriggerRepository repository = await OpenFaultedAsync(
            directory.DatabasePath,
            new NthFaultInjector(faultPoint, occurrence: 2));
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        TriggerLifecycleHandoffIdentity identity = new(execution.ExecutionId, 0, CurrentEpoch);

        if (faultPoint == TriggerPersistenceFaultPoint.BeforeLifecycleHandoffCommit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AcknowledgeReleaseAsync(identity, CancellationToken.None));
            Assert.False(request.Handoff!.WaitForReleaseAsync(CancellationToken.None).IsCompleted);
            await coordinator.AcknowledgeReleaseAsync(identity, CancellationToken.None);
        }
        else
        {
            await coordinator.AcknowledgeReleaseAsync(identity, CancellationToken.None);
        }

        await request.Handoff!.WaitForReleaseAsync(CancellationToken.None);
        Assert.Equal(
            TriggerLifecycleHandoffState.ReleaseAcknowledged,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeLifecycleHandoffCommit, false)]
    [InlineData(TriggerPersistenceFaultPoint.AfterLifecycleHandoffCommit, true)]
    public async Task ProcessAsync_ShutdownStartedCommitCut_StopsOnlyAfterDurableStart(
        TriggerPersistenceFaultPoint faultPoint,
        bool completes)
    {
        using TriggerExitDirectory directory = new();
        (_, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        SqliteTriggerRepository repository = await OpenFaultedAsync(
            directory.DatabasePath,
            new NthFaultInjector(faultPoint, occurrence: 3));
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await coordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, 0, CurrentEpoch),
            CancellationToken.None);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace));

        if (completes)
        {
            await runner.ProcessAsync(request, CancellationToken.None);
            Assert.Equal(["host-stop-enter", "host-stop-unwound", "host-dispose"], trace);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ProcessAsync(request, CancellationToken.None));
            Assert.Empty(trace);
        }

        TriggerLifecycleHandoff handoff = await ReadHandoffAsync(repository, execution.ExecutionId, 0);
        Assert.Equal(
            completes ? TriggerLifecycleHandoffState.Succeeded : TriggerLifecycleHandoffState.ReleaseAcknowledged,
            handoff.State);
    }

    [Theory]
    [InlineData(TriggerPersistenceFaultPoint.BeforeLifecycleHandoffCommit, false)]
    [InlineData(TriggerPersistenceFaultPoint.AfterLifecycleHandoffCommit, true)]
    public async Task ProcessAsync_SuccessCommitCut_IsRecoveredByNextEpochWithoutAnotherExit(
        TriggerPersistenceFaultPoint faultPoint,
        bool completes)
    {
        using TriggerExitDirectory directory = new();
        (_, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        SqliteTriggerRepository repository = await OpenFaultedAsync(
            directory.DatabasePath,
            new NthFaultInjector(faultPoint, occurrence: 4));
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator coordinator = CreateCoordinator(repository, requests);
        await coordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await coordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, 0, CurrentEpoch),
            CancellationToken.None);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new FakeHost(trace));

        if (completes)
        {
            await runner.ProcessAsync(request, CancellationToken.None);
            Assert.Equal(["host-stop-enter", "host-stop-unwound", "host-dispose"], trace);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ProcessAsync(request, CancellationToken.None));
            Assert.Equal(["host-stop-enter", "host-stop-unwound"], trace);
            Assert.Equal(
                TriggerLifecycleHandoffState.ShutdownStarted,
                (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
            ApplicationLifetimeRequestChannel nextEpochRequests = new();
            TriggerLifecycleHandoffCoordinator nextEpoch = new(
                repository,
                nextEpochRequests,
                new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1)),
                PriorEpoch);

            TriggerActionProbeResult recovered = await nextEpoch.ProbeAsync(
                action,
                CancellationToken.None);

            Assert.Equal(TriggerActionProbeStatus.Desired, recovered.Status);
            Assert.False(nextEpochRequests.HasAcceptedRequest);
        }

        Assert.Equal(
            TriggerLifecycleHandoffState.Succeeded,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
    }

    [Fact]
    public async Task ProcessAsync_RecoveryOnly_StopsWithoutCompetingNetworkMutation()
    {
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator handoffCoordinator = CreateCoordinator(repository, requests);
        await handoffCoordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await handoffCoordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, 0, CurrentEpoch),
            CancellationToken.None);
        MutationAdmissionBarrier admission = new();
        admission.EnterRecoveryOnly();
        FakeShutdownNetwork network = new();
        RuntimeLifecycleCoordinator runtime = CreateRuntimeCoordinator(admission, network);
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new RuntimeCoordinatorHost(runtime, []));

        await runner.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(0, network.CallCount);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, admission.State);
        Assert.Equal(
            TriggerLifecycleHandoffState.Succeeded,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
    }

    [Theory]
    [InlineData((int)MutationOutcome.CommittedRecoveryRequired, true)]
    [InlineData((int)MutationOutcome.Compensated, false)]
    public async Task ProcessAsync_ShutdownMutationOutcome_ClassifiesHandoffAndHostOwnership(
        int outcomeValue,
        bool prepared)
    {
        MutationOutcome outcome = (MutationOutcome)outcomeValue;
        using TriggerExitDirectory directory = new();
        (SqliteTriggerRepository repository, TriggerExecution execution, TriggerOutboxAction action) =
            await SeedRunningExitAsync(directory.DatabasePath, CurrentEpoch);
        ApplicationLifetimeRequestChannel requests = new();
        TriggerLifecycleHandoffCoordinator handoffCoordinator = CreateCoordinator(repository, requests);
        await handoffCoordinator.HandOffAsync(action, CancellationToken.None);
        ApplicationLifetimeRequest request = await requests.ReadAsync(CancellationToken.None);
        await handoffCoordinator.AcknowledgeReleaseAsync(
            new TriggerLifecycleHandoffIdentity(execution.ExecutionId, 0, CurrentEpoch),
            CancellationToken.None);
        MutationAdmissionBarrier admission = new();
        FakeShutdownNetwork network = new()
        {
            Outcome = outcome,
            ErrorCode = prepared ? "committed-recovery-required" : "shutdown-compensated",
        };
        RuntimeLifecycleCoordinator runtime = CreateRuntimeCoordinator(admission, network);
        List<string> trace = [];
        ProcessLifetimeRunner runner = new();
        runner.AttachHost(new RuntimeCoordinatorHost(runtime, trace));

        if (prepared)
        {
            await runner.ProcessAsync(request, CancellationToken.None);
            Assert.False(runner.HasAttachedHost);
            Assert.Contains("host-dispose", trace);
        }
        else
        {
            await Assert.ThrowsAsync<RuntimeShutdownNotPreparedException>(
                () => runner.ProcessAsync(request, CancellationToken.None));
            Assert.True(runner.HasAttachedHost);
            Assert.DoesNotContain("host-dispose", trace);
        }

        Assert.Equal(1, network.CallCount);
        Assert.Equal(
            prepared ? TriggerLifecycleHandoffState.Succeeded : TriggerLifecycleHandoffState.Failed,
            (await ReadHandoffAsync(repository, execution.ExecutionId, 0)).State);
        Assert.Equal(
            prepared ? MutationAdmissionState.ClosedForShutdown : MutationAdmissionState.Open,
            admission.State);
    }

    private static TriggerLifecycleHandoffCoordinator CreateCoordinator(
        ITriggerRepository repository,
        IApplicationLifetimeRequestSink requests)
    {
        return new TriggerLifecycleHandoffCoordinator(
            repository,
            requests,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            CurrentEpoch);
    }

    private static async Task<(SqliteTriggerRepository Repository, TriggerExecution Execution, TriggerOutboxAction Action)>
        SeedRunningExitAsync(string databasePath, Guid processEpoch)
    {
        SqliteTriggerRepository repository = new(databasePath);
        Assert.True((await repository.OpenAsync(CancellationToken.None)).IsSucceeded);
        TriggerTaskDefinition definition = new(
            "exit-task",
            1,
            "Exit task",
            isEnabled: true,
            [
                new TriggerCondition(
                    "event",
                    TriggerConditionKind.Event,
                    new EventConditionParameters(TriggerEventKind.AppEntered)),
            ],
            [new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters())]);
        Assert.True((await repository.ReplaceDefinitionsAsync(
            new TriggerDefinitionWriteRequest(0, [definition]),
            CancellationToken.None)).IsSucceeded);
        TriggerRepositorySnapshot snapshot = Assert.IsType<TriggerRepositorySnapshot>(
            (await repository.ReadSnapshotAsync(CancellationToken.None)).Value);
        TriggerTaskState state = snapshot.Tasks[0].State;
        TriggerPersistenceResult<TriggerExecution> committed = await repository.TryCommitExecutionAsync(
            new TriggerExecutionCommitRequest(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                definition,
                state.Version,
                state,
                DateTimeOffset.UnixEpoch,
                processEpoch),
            CancellationToken.None);
        TriggerExecution execution = Assert.IsType<TriggerExecution>(committed.Value);
        TriggerOutboxAction action = (await ReadActionsAsync(repository, execution.ExecutionId))[0];
        TriggerPersistenceResult<TriggerOutboxAction> running = await repository.TransitionOutboxAsync(
            new TriggerOutboxTransition(
                execution.ExecutionId,
                action.ActionIndex,
                TriggerOutboxState.Pending,
                TriggerOutboxState.Running),
            CancellationToken.None);
        Assert.True(running.IsSucceeded, running.Diagnostic?.Code);
        return (repository, execution, Assert.IsType<TriggerOutboxAction>(running.Value));
    }

    private static async Task<IReadOnlyList<TriggerOutboxAction>> ReadActionsAsync(
        ITriggerRepository repository,
        Guid executionId)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> result =
            await repository.ReadExecutionActionsAsync(executionId, CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsAssignableFrom<IReadOnlyList<TriggerOutboxAction>>(result.Value);
    }

    private static async Task<TriggerLifecycleHandoff> ReadHandoffAsync(
        ITriggerRepository repository,
        Guid executionId,
        int actionIndex)
    {
        TriggerPersistenceResult<TriggerLifecycleHandoff> result =
            await repository.ReadLifecycleHandoffAsync(executionId, actionIndex, CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Diagnostic?.Code);
        return Assert.IsType<TriggerLifecycleHandoff>(result.Value);
    }

    private static async Task<SqliteTriggerRepository> OpenFaultedAsync(
        string databasePath,
        ITriggerPersistenceFaultInjector faultInjector)
    {
        SqliteTriggerRepository repository = new(databasePath, faultInjector);
        TriggerPersistenceResult<TriggerRepositorySnapshot> opened = await repository.OpenAsync(
            CancellationToken.None);
        Assert.True(opened.IsSucceeded, opened.Diagnostic?.Code);
        return repository;
    }

    private static RuntimeLifecycleCoordinator CreateRuntimeCoordinator(
        MutationAdmissionBarrier admission,
        IRuntimeShutdownNetworkCoordinator network)
    {
        return new RuntimeLifecycleCoordinator(
            admission,
            network,
            () => NetworkIntent.Shutdown(
                ClashSharpMode.Disabled,
                transparentProxyEnabled: false,
                mixedPort: 7890),
            []);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public enum LifetimeCallbackFaultPoint
    {
        WaitForRelease,
        MarkShutdownStarted,
        MarkShutdownSucceeded,
        MarkShutdownFailed,
    }

    private sealed class FailOnceCallbackRequestSink(
        ApplicationLifetimeRequestChannel requests,
        LifetimeCallbackFaultPoint faultPoint) : IApplicationLifetimeRequestSink
    {
        public bool TryRequest(ApplicationLifetimeRequest request)
        {
            return requests.TryRequest(request.Handoff is null
                ? request
                : ApplicationLifetimeRequest.Exit(
                    request.Source,
                    new FailOnceCallbackHandoff(request.Handoff, faultPoint)));
        }
    }

    private sealed class FailOnceCallbackHandoff(
        IApplicationLifetimeHandoff inner,
        LifetimeCallbackFaultPoint faultPoint) : IApplicationLifetimeHandoff
    {
        private int _remainingFailures = 1;

        public string IdempotencyKey => inner.IdempotencyKey;

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await inner.WaitForReleaseAsync(cancellationToken);
            ThrowOnce(LifetimeCallbackFaultPoint.WaitForRelease);
        }

        public async Task MarkShutdownStartedAsync(CancellationToken cancellationToken)
        {
            ThrowOnce(LifetimeCallbackFaultPoint.MarkShutdownStarted);
            await inner.MarkShutdownStartedAsync(cancellationToken);
        }

        public async Task MarkShutdownSucceededAsync(CancellationToken cancellationToken)
        {
            ThrowOnce(LifetimeCallbackFaultPoint.MarkShutdownSucceeded);
            await inner.MarkShutdownSucceededAsync(cancellationToken);
        }

        public async Task MarkShutdownFailedAsync(
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode,
            CancellationToken cancellationToken)
        {
            ThrowOnce(LifetimeCallbackFaultPoint.MarkShutdownFailed);
            await inner.MarkShutdownFailedAsync(failureKind, diagnosticCode, cancellationToken);
        }

        private void ThrowOnce(LifetimeCallbackFaultPoint current)
        {
            if (faultPoint == current
                && Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException($"Injected {current} callback failure.");
            }
        }
    }

    private sealed class FakeHost(List<string> trace) : IApplicationHost
    {
        public Exception? StopException { get; init; }

        public Task<StartupStepResult> StartAsync(
            AppLaunchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(StartupStepResult.Succeeded());

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("host-stop-enter");
            if (StopException is not null)
            {
                throw StopException;
            }

            await Task.Yield();
            trace.Add("host-stop-unwound");
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NthFaultInjector(
        TriggerPersistenceFaultPoint target,
        int occurrence = 1) : ITriggerPersistenceFaultInjector
    {
        private int _remaining = occurrence;

        public Task InjectAsync(
            TriggerPersistenceFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (faultPoint == target && Interlocked.Decrement(ref _remaining) == 0)
            {
                throw new IOException("Injected lifecycle handoff commit failure.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ExitOnlyRuntime(ITriggerLifecycleHandoff handoff) : ITriggerActionRuntime
    {
        public Task<TriggerActionProbeResult> ProbeAsync(
            TriggerOutboxAction action,
            CancellationToken cancellationToken) => handoff.ProbeAsync(action, cancellationToken);

        public Task<TriggerActionApplyResult> ApplyAsync(
            TriggerOutboxAction action,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken) => handoff.HandOffAsync(action, cancellationToken);
    }

    private sealed class FakeShutdownNetwork : IRuntimeShutdownNetworkCoordinator
    {
        public int CallCount { get; private set; }

        public MutationOutcome Outcome { get; init; } = MutationOutcome.Succeeded;

        public string? ErrorCode { get; init; }

        public Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(
            NetworkIntent intent,
            MutationAdmissionLease admissionLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new MutationResult<NetworkTransitionResult>(
                Guid.NewGuid(),
                Outcome,
                new NetworkTransitionResult(
                    ClashSharpMode.Disabled,
                    false,
                    false,
                    false,
                    7890,
                    "disabled"),
                ErrorCode));
        }
    }

    private sealed class RuntimeCoordinatorHost(
        RuntimeLifecycleCoordinator runtime,
        List<string> trace) : IApplicationHost
    {
        public Task<StartupStepResult> StartAsync(
            AppLaunchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(StartupStepResult.Succeeded());

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("host-stop-enter");
            RuntimeShutdownResult result = await runtime.ShutdownAsync(cancellationToken);
            if (result.Outcome != RuntimeShutdownOutcome.PreparedForHostDisposal)
            {
                throw new RuntimeShutdownNotPreparedException(result);
            }

            trace.Add("host-stop-unwound");
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TriggerExitDirectory : IDisposable
    {
        private const int MaximumDeleteAttempts = 5;
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.TriggerExitTests",
            Guid.NewGuid().ToString("N"));

        public TriggerExitDirectory() => Directory.CreateDirectory(_path);

        public string DatabasePath => Path.Combine(_path, "Triggers.db");

        public void Dispose()
        {
            for (int attempt = 1; attempt <= MaximumDeleteAttempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(_path))
                    {
                        Directory.Delete(_path, recursive: true);
                    }

                    return;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException
                        && attempt < MaximumDeleteAttempts)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
                }
            }
        }
    }
}
