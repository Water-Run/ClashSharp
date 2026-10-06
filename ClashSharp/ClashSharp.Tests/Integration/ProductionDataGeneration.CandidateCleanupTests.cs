extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task FailedManifestPromotion_ReleasesUnpublishedCandidateDirectoryAfterCompensation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        DataGenerationManifestSnapshot baseline = fixture.Manager.CurrentManifest;

        await Assert.ThrowsAsync<IOException>(() => fixture.CreateReplacementCoordinator(
            new FailingPromotionStore(directory.Store, afterPromotion: false)).ResetAllSettingsAsync(CancellationToken.None));

        DataGenerationDescriptor candidate = fixture.Containers[1].Generation;
        Assert.False(Directory.Exists(candidate.RootPath));
        Assert.True(File.Exists(Path.Combine(baseline.Descriptor.RootPath, "ProfileCatalog.json")));
        Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Equal(23456, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
        Assert.Null(await new UiData.FileGenerationReplacementJournal(directory.RootPath).ReadPendingAsync(CancellationToken.None));
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(RuntimeOf(fixture.Containers[0]).IsExecutionPublished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCandidateDirectory_IsAcceptedOnlyByACompletedCheckpoint(bool completed)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
        await using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, CancellationToken.None);
        Guid operation = Guid.NewGuid();
        await journal.BeginAsync(operation, fixture.Manager.CurrentManifest,
            await RuntimeOf(fixture).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        await journal.SetCandidateAsync(operation, candidate, CancellationToken.None);
        if (completed) { await journal.CompleteAsync(operation, CancellationToken.None); }

        directory.Policy.ValidateDescriptor(candidate);
        Assert.True(ClashSharp.Infrastructure.Data.DataGenerationPathPolicy.IsContainedBy(directory.RootPath, candidate.RootPath));
        File.Delete(Assert.Single(Directory.EnumerateFiles(candidate.RootPath)));
        Directory.Delete(candidate.RootPath, recursive: false);

        if (completed)
        {
            Assert.Null(await journal.ReadPendingAsync(CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<DataGenerationStoreException>(() => journal.ReadPendingAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task CandidateCleanup_RequiresCompletedRecoveryAndSupportsExactRetry()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var context = await PrepareAbortedCandidateAsync(fixture, directory, complete: false);
        await using MutationAdmissionLease lease = context.Lease;
        await using DataGenerationTransition transition = context.Transition;
        UiData.GenerationCandidateDirectoryCleanup cleanup = new(directory.RootPath, fixture.Admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.DeleteAbortedAsync(
            transition, context.Journal, lease, CancellationToken.None));
        Assert.True(Directory.Exists(context.Candidate.RootPath));
        Assert.NotNull(await context.Journal.ReadPendingAsync(CancellationToken.None));

        await context.Journal.CompleteAsync(context.OperationId, CancellationToken.None);
        await cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None);
        await cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None);
        Assert.False(Directory.Exists(context.Candidate.RootPath));
        Assert.Equal(transition.BaselineManifest.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Fact]
    public async Task CandidateCleanup_PinsDirectoryAncestorsAndManifestUntilDeletionFinishes()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var context = await PrepareAbortedCandidateAsync(fixture, directory);
        await using MutationAdmissionLease lease = context.Lease;
        await using DataGenerationTransition transition = context.Transition;
        string child = Path.Combine(context.Candidate.RootPath, "nested", "child");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "discarded.txt"), "candidate");
        string temporaryPointer = Path.Combine(directory.Policy.DataRootPath, "cleanup-pointer-test.tmp");
        byte[] pointer = await File.ReadAllBytesAsync(directory.Policy.CurrentManifestPath);
        await File.WriteAllBytesAsync(temporaryPointer, pointer);
        string temporaryJournal = Path.Combine(directory.Policy.DataRootPath, "cleanup-journal-test.tmp");
        await File.WriteAllBytesAsync(temporaryJournal, await File.ReadAllBytesAsync(context.Journal.Path));
        int checkpoints = 0;
        UiData.GenerationCandidateDirectoryCleanup cleanup = new(directory.RootPath, fixture.Admission, _ =>
        {
            ++checkpoints;
            Assert.Throws<IOException>(() => Directory.Move(context.Candidate.RootPath, context.Candidate.RootPath + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(directory.Policy.GenerationsRootPath,
                Path.Combine(directory.Policy.DataRootPath, "moved-generations")));
            Assert.Throws<IOException>(() => File.Replace(temporaryPointer, directory.Policy.CurrentManifestPath, null));
            Assert.Throws<IOException>(() => File.Replace(temporaryJournal, context.Journal.Path, null));
        });

        await cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None);

        Assert.Equal(1, checkpoints);
        Assert.False(Directory.Exists(context.Candidate.RootPath));
        Assert.Equal(pointer, await File.ReadAllBytesAsync(directory.Policy.CurrentManifestPath));
    }

    [Fact]
    public async Task CandidateCleanup_RejectsRedirectedChildrenBeforeRemovingAnyFile()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory outside = new();
        await using Fixture fixture = new(directory);
        var context = await PrepareAbortedCandidateAsync(fixture, directory);
        await using MutationAdmissionLease lease = context.Lease;
        await using DataGenerationTransition transition = context.Transition;
        Directory.CreateDirectory(outside.RootPath);
        string preserved = Path.Combine(outside.RootPath, "preserved.txt");
        await File.WriteAllTextAsync(preserved, "outside");
        string candidateFile = Path.Combine(context.Candidate.RootPath, "owned.txt");
        await File.WriteAllTextAsync(candidateFile, "candidate");
        string redirect = Path.Combine(context.Candidate.RootPath, "redirect");
        Directory.CreateSymbolicLink(redirect, outside.RootPath);
        try
        {
            UiData.GenerationCandidateDirectoryCleanup cleanup = new(directory.RootPath, fixture.Admission);
            DataGenerationStoreException failure = await Assert.ThrowsAsync<DataGenerationStoreException>(() =>
                cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None));
            Assert.Equal(DataGenerationStoreError.UnsafePath, failure.Error);
            Assert.Equal("outside", await File.ReadAllTextAsync(preserved));
            Assert.Equal("candidate", await File.ReadAllTextAsync(candidateFile));
        }
        finally { Directory.Delete(redirect, recursive: false); }
    }

    [Fact]
    public async Task CandidateCleanup_LockedFileKeepsIdentityAndCanBeRemovedAfterRelease()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var context = await PrepareAbortedCandidateAsync(fixture, directory);
        await using MutationAdmissionLease lease = context.Lease;
        await using DataGenerationTransition transition = context.Transition;
        string locked = Path.Combine(context.Candidate.RootPath, "locked.txt");
        await File.WriteAllTextAsync(locked, "retained until released");
        UiData.GenerationCandidateDirectoryCleanup cleanup = new(directory.RootPath, fixture.Admission);
        using (FileStream handle = new(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None));
            directory.Policy.ValidateDescriptor(context.Candidate);
            Assert.True(File.Exists(locked));
            Assert.Null(await context.Journal.ReadPendingAsync(CancellationToken.None));
        }

        await cleanup.DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None);
        Assert.False(Directory.Exists(context.Candidate.RootPath));
    }

    [Fact]
    public async Task CandidateCleanup_RefusesADifferentDurableGenerationWithoutDeletingTheCandidate()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var context = await PrepareAbortedCandidateAsync(fixture, directory);
        await using MutationAdmissionLease lease = context.Lease;
        await using DataGenerationTransition transition = context.Transition;
        DataGenerationDescriptor winner = directory.CreateGeneration(2);
        _ = await directory.Store.PromoteAsync(winner, transition.BaselineManifest.ContentHash, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new UiData.GenerationCandidateDirectoryCleanup(directory.RootPath, fixture.Admission)
            .DeleteAbortedAsync(transition, context.Journal, lease, CancellationToken.None));

        Assert.True(Directory.Exists(context.Candidate.RootPath));
        directory.Policy.ValidateDescriptor(winner);
    }

    [Fact]
    public async Task FailedCandidateFileCleanup_PreservesVerifiedCompensationAndReopensMutations()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.MixedPort.Value] = 23456;
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        DataGenerationManifestSnapshot baseline = fixture.Manager.CurrentManifest;
        FileStream? locked = null;
        FailingPromotionStore store = new(directory.Store, afterPromotion: false, candidate =>
        {
            string path = Path.Combine(candidate.RootPath, "locked.txt");
            File.WriteAllText(path, "still owned by the test");
            locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
                fixture.CreateReplacementCoordinator(store).ResetAllSettingsAsync(CancellationToken.None));
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Equal(baseline.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
            Assert.Equal(23456, (await network.ReadConfigurationAsync(CancellationToken.None)).MixedPort);
            Assert.True(RuntimeOf(fixture.Containers[0]).IsExecutionPublished);
            Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
            directory.Policy.ValidateDescriptor(fixture.Containers[1].Generation);
            Assert.Null(await new UiData.FileGenerationReplacementJournal(directory.RootPath).ReadPendingAsync(CancellationToken.None));
        }
        finally { locked?.Dispose(); }
    }

    private static async Task<(MutationAdmissionLease Lease, DataGenerationTransition Transition,
        UiData.FileGenerationReplacementJournal Journal, DataGenerationDescriptor Candidate, Guid OperationId)>
        PrepareAbortedCandidateAsync(Fixture fixture, DataGenerationTestDirectory directory, bool complete = true)
    {
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        DataGenerationTransition? transition = null;
        try
        {
            UiData.FileGenerationReplacementJournal journal = new(directory.RootPath);
            Guid operation = Guid.NewGuid();
            await journal.BeginAsync(operation, fixture.Manager.CurrentManifest,
                await RuntimeOf(fixture.Containers[0]).ExternalState.CaptureAdmittedAsync(lease, CancellationToken.None), CancellationToken.None);
            transition = await fixture.Manager.BeginDrainAsync(fixture.Manager.CurrentManifest.ContentHash, CancellationToken.None);
            DataGenerationDescriptor candidate = await fixture.CreateCandidatePreparer().StageResetAdmittedAsync(transition, lease, CancellationToken.None);
            await journal.SetCandidateAsync(operation, candidate, CancellationToken.None);
            await transition.AbortAsync(directory.Store, CancellationToken.None);
            if (complete) { await journal.CompleteAsync(operation, CancellationToken.None); }
            return (lease, transition, journal, candidate, operation);
        }
        catch
        {
            if (transition is not null) { await transition.AbortAsync(directory.Store, CancellationToken.None); }
            await lease.DisposeAsync();
            throw;
        }
    }
}
