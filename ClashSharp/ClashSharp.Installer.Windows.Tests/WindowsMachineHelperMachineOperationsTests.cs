using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperMachineOperationsTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string FreshToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ExistingToken =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ForeignToken =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    public enum MissingProfileUninstallPath
    {
        PrepareExecute,
        PrepareCommittedReplay,
        RemoveExecute,
        RemoveCommittedReplay,
        FinalVerification,
    }

    [Fact]
    public async Task InstallPrepareReservesFreshAssociationAfterServiceFence()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(
            InstallerMachineAssociation.Create(TargetSid, FreshToken),
            backend.Association.Association);
        AssertOrdered(
            backend.Calls,
            "service:stop-fence",
            "association:write",
            "service:verify-prepared",
            "association:verify-exact");
    }

    [Fact]
    public async Task InstallPrepareReusesExistingOwnerToken()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            ServiceAssociation = existing,
            ServiceInstalled = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(existing, backend.Association.Association);
        Assert.Equal(ExistingToken, backend.LastServicePlan!.Association.AuthenticationToken);
        Assert.True(backend.ServicePrepared);
    }

    [Fact]
    public async Task OrdinaryInstallRejectsUnownedResidueBeforeMutation()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: false)
        {
            PayloadPresent = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => operations.PrepareAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal("installer.machine.reassociation_required", exception.DiagnosticCode);
        Assert.DoesNotContain("service:stop-fence", backend.Calls);
        Assert.DoesNotContain("association:write", backend.Calls);
    }

    [Fact]
    public async Task ExplicitRepairReplacesInvalidAssociationOnlyAfterFence()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Repair,
            TargetSid) with
        {
            AllowReassociation = true,
        };
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Invalid(),
            rootsAbsent: false)
        {
            PayloadPresent = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Equal(
            InstallerMachineAssociation.Create(TargetSid, FreshToken),
            backend.Association.Association);
        AssertOrdered(backend.Calls, "service:stop-fence", "association:write");
    }

    [Fact]
    public async Task ExplicitRepairDoesNotOverwriteEvidenceForForeignService()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Repair,
            TargetSid) with
        {
            AllowReassociation = true,
        };
        InstallerMachineAssociation foreign = InstallerMachineAssociation.Create(
            OtherSid,
            ForeignToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(foreign),
            rootsAbsent: false)
        {
            ServiceAssociation = foreign,
            ServiceInstalled = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => operations.PrepareAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal("installer.machine.existing_service_not_owned", exception.DiagnosticCode);
        Assert.Equal(foreign, backend.Association.Association);
        Assert.DoesNotContain("association:write", backend.Calls);
    }

    [Fact]
    public async Task PrepareCommittedReplayIsObservationOnly()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            ServiceAssociation = existing,
            ServicePrepared = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            CancellationToken.None);

        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Contains("roots:read", backend.Calls);
        Assert.Contains("association:verify-exact", backend.Calls);
        Assert.Contains("service:verify-prepared", backend.Calls);
    }

    [Fact]
    public async Task ApplyStagesBeforeFenceThenPromotesAndStarts()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = InstalledOwnerBackend(existing);
        backend.PayloadPresent = false;
        backend.PayloadInstalled = false;
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.ApplyAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        AssertOrdered(
            backend.Calls,
            "payload:stage",
            "service:stop-fence",
            "payload:promote",
            "service:configure-start",
            "payload:verify-installed",
            "service:verify-installed");
    }

    [Fact]
    public async Task ApplyCommittedReplayDoesNotMutateInstalledMachine()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        FakeBackend backend = InstalledOwnerBackend(existing);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.ApplyAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            CancellationToken.None);

        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Contains("payload:verify-installed", backend.Calls);
        Assert.Contains("service:verify-installed", backend.Calls);
    }

    [Fact]
    public async Task ApplyRejectsMissingReservedAssociationBeforePayloadMutation()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: false);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => operations.ApplyAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal("installer.machine.association_not_exact", exception.DiagnosticCode);
        Assert.DoesNotContain("payload:stage", backend.Calls);
    }

    [Fact]
    public async Task UninstallPrepareFencesOwnedServiceWithoutDeletingAssociation()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        FakeBackend backend = InstalledOwnerBackend(existing);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.True(backend.ServicePrepared);
        Assert.Equal(existing, backend.Association.Association);
        Assert.DoesNotContain("association:delete", backend.Calls);
        Assert.DoesNotContain("payload:remove", backend.Calls);
    }

    [Fact]
    public async Task UninstallPrepareAllowsMissingAssociationOnlyWhenMachineIsAlreadyAbsent()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Contains("service:verify-absent", backend.Calls);
        Assert.Contains("payload:verify-absent", backend.Calls);
        Assert.DoesNotContain("association:write", backend.Calls);
    }

    [Fact]
    public async Task UninstallPrepareRejectsMissingAssociationWithPayloadResidue()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: false)
        {
            PayloadPresent = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => operations.PrepareAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal("installer.machine.removal_not_authorized", exception.DiagnosticCode);
        Assert.DoesNotContain("payload:remove", backend.Calls);
    }

    [Fact]
    public async Task RemoveDeletesAssociationLastAndReleasesRootLeaseBeforeCleanup()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        FakeBackend backend = InstalledOwnerBackend(existing);
        backend.ServicePrepared = true;
        backend.ServiceInstalled = false;
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.RemoveAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        AssertOrdered(
            backend.Calls,
            "service:stop-delete",
            "payload:remove",
            "association:delete",
            "roots:remove-empty",
            "service:verify-absent",
            "payload:verify-absent",
            "roots:verify-absent");
        Assert.Equal(0, backend.ActiveRootGuards);
    }

    [Fact]
    public async Task RemoveReplayAfterAssociationWasDeletedRequiresAllOtherMachineStateAbsent()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.RemoveAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        Assert.Contains("roots:remove-empty", backend.Calls);
        Assert.Equal(InstallerMachineAssociationStatus.Missing, backend.Association.Status);
    }

    [Fact]
    public async Task RemoveRejectsMissingAssociationWhenPayloadStillExists()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: false)
        {
            PayloadPresent = true,
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => operations.RemoveAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None));

        Assert.Equal("installer.machine.removal_not_authorized", exception.DiagnosticCode);
        Assert.DoesNotContain("payload:remove", backend.Calls);
        Assert.DoesNotContain("association:delete", backend.Calls);
    }

    [Fact]
    public async Task RemoveCommittedReplayIsStrictlyReadOnly()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.RemoveAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            CancellationToken.None);

        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Collection(
            backend.Calls.Where(static call =>
                call.Contains(":verify-", StringComparison.Ordinal)),
            call => Assert.Equal("service:verify-absent", call),
            call => Assert.Equal("payload:verify-absent", call),
            call => Assert.Equal("roots:verify-absent", call));
    }

    [Fact]
    public async Task RemovalAuthorizationReplayAcceptsAlreadyRemovedPostconditionWithoutCreatingRoots()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.PrepareAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
            CancellationToken.None);

        Assert.DoesNotContain("roots:create", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Contains("roots:verify-absent", backend.Calls);
    }

    [Theory]
    [InlineData(MissingProfileUninstallPath.PrepareExecute)]
    [InlineData(MissingProfileUninstallPath.PrepareCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.RemoveExecute)]
    [InlineData(MissingProfileUninstallPath.RemoveCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.FinalVerification)]
    public async Task MissingTargetProfileAcceptsOnlyProfileIndependentRemovedPostcondition(
        MissingProfileUninstallPath path)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true)
        {
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await InvokeUninstallPathAsync(
            operations,
            request,
            fixture.Manifest,
            path);

        Assert.Contains("profile:resolve", backend.Calls);
        Assert.Contains("machine:verify-profile-independent-removed", backend.Calls);
        Assert.DoesNotContain("plan:create", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
    }

    [Theory]
    [InlineData(MissingProfileUninstallPath.PrepareExecute)]
    [InlineData(MissingProfileUninstallPath.PrepareCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.RemoveExecute)]
    [InlineData(MissingProfileUninstallPath.RemoveCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.FinalVerification)]
    public async Task MissingTargetProfileDoesNotAuthorizeRemainingMachineState(
        MissingProfileUninstallPath path)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: false)
        {
            PayloadPresent = true,
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InvokeUninstallPathAsync(
                    operations,
                    request,
                    fixture.Manifest,
                    path));

        Assert.Equal(
            "installer.machine.root_removal_verification_failed",
            exception.DiagnosticCode);
        Assert.Contains("machine:verify-profile-independent-removed", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
    }

    [Theory]
    [InlineData(MissingProfileUninstallPath.PrepareExecute)]
    [InlineData(MissingProfileUninstallPath.PrepareCommittedReplay)]
    public async Task MissingTargetProfileUsesExactAssociationForReadOnlyRemovalAuthorization(
        MissingProfileUninstallPath path)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            PayloadPresent = true,
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await InvokeUninstallPathAsync(
            operations,
            request,
            fixture.Manifest,
            path);

        Assert.Contains("plan:create-profile-independent", backend.Calls);
        Assert.Contains("roots:read", backend.Calls);
        Assert.Contains("service:verify-absent", backend.Calls);
        Assert.Contains("association:verify-exact", backend.Calls);
        Assert.DoesNotContain("plan:create", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Equal(existing, backend.Association.Association);
        Assert.True(backend.PayloadPresent);
        Assert.False(backend.RootsAbsent);
    }

    [Fact]
    public async Task MissingTargetProfileRemovesOwnedFixedPayloadOnlyWhenServiceIsAbsent()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            PayloadPresent = true,
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        await operations.RemoveAsync(
            request,
            new FakeReleaseLease(request, fixture.Manifest),
            InstallerMachineHelperSessionDisposition.Execute,
            CancellationToken.None);

        AssertOrdered(
            backend.Calls,
            "plan:create-profile-independent",
            "service:verify-absent",
            "payload:remove",
            "association:delete",
            "roots:remove-empty");
        Assert.DoesNotContain("plan:create", backend.Calls);
        Assert.DoesNotContain("roots:create", backend.Calls);
        Assert.DoesNotContain("service:stop-fence", backend.Calls);
        Assert.DoesNotContain("service:stop-delete", backend.Calls);
        Assert.Equal(
            InstallerMachineAssociationStatus.Missing,
            backend.Association.Status);
        Assert.False(backend.PayloadPresent);
        Assert.True(backend.RootsAbsent);
    }

    [Theory]
    [InlineData(MissingProfileUninstallPath.PrepareExecute)]
    [InlineData(MissingProfileUninstallPath.PrepareCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.RemoveExecute)]
    public async Task MissingTargetProfileNeverMutatesThroughAStillExistingService(
        MissingProfileUninstallPath path)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            ServiceAssociation = existing,
            ServiceInstalled = true,
            PayloadPresent = true,
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InvokeUninstallPathAsync(
                    operations,
                    request,
                    fixture.Manifest,
                    path));

        Assert.Equal(
            "installer.machine.service_removal_verification_failed",
            exception.DiagnosticCode);
        Assert.Contains("service:verify-absent", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
        Assert.Equal(existing, backend.Association.Association);
        Assert.True(backend.PayloadPresent);
    }

    [Theory]
    [InlineData(MissingProfileUninstallPath.RemoveCommittedReplay)]
    [InlineData(MissingProfileUninstallPath.FinalVerification)]
    public async Task MissingTargetProfileFinalReadOnlyPathsRejectOwnedResidue(
        MissingProfileUninstallPath path)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Valid(existing),
            rootsAbsent: false)
        {
            PayloadPresent = true,
            TargetProfileFailureDiagnostic = "installer.machine.target_profile_missing",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                InvokeUninstallPathAsync(
                    operations,
                    request,
                    fixture.Manifest,
                    path));

        Assert.Equal(
            "installer.machine.root_removal_verification_failed",
            exception.DiagnosticCode);
        Assert.Contains("machine:verify-profile-independent-removed", backend.Calls);
        Assert.DoesNotContain("plan:create-profile-independent", backend.Calls);
        Assert.DoesNotContain(backend.Calls, IsMutation);
    }

    [Fact]
    public async Task InvalidTargetProfileEvidenceIsNeverTreatedAsMissingProfile()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true)
        {
            TargetProfileFailureDiagnostic =
                "installer.machine.target_profile_path_invalid",
        };
        var operations = new WindowsMachineHelperMachineOperations(backend);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                operations.VerifyAsync(
                    request,
                    new FakeReleaseLease(request, fixture.Manifest),
                    CancellationToken.None));

        Assert.Equal(
            "installer.machine.target_profile_path_invalid",
            exception.DiagnosticCode);
        Assert.DoesNotContain(
            "machine:verify-profile-independent-removed",
            backend.Calls);
    }

    [Fact]
    public async Task FinalVerificationUsesInstalledOrRemovedPostconditionsWithoutMutation()
    {
        using var fixture = Fixture();
        InstallerMachineAssociation existing = InstallerMachineAssociation.Create(
            TargetSid,
            ExistingToken);
        InstallerRequest install = fixture.Request(targetSid: TargetSid);
        FakeBackend installed = InstalledOwnerBackend(existing);
        var installOperations = new WindowsMachineHelperMachineOperations(installed);

        await installOperations.VerifyAsync(
            install,
            new FakeReleaseLease(install, fixture.Manifest),
            CancellationToken.None);

        Assert.DoesNotContain(installed.Calls, IsMutation);

        InstallerRequest uninstall = fixture.Request(
            InstallerOperation.Uninstall,
            TargetSid);
        var removed = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var removeOperations = new WindowsMachineHelperMachineOperations(removed);

        await removeOperations.VerifyAsync(
            uninstall,
            new FakeReleaseLease(uninstall, fixture.Manifest),
            CancellationToken.None);

        Assert.DoesNotContain(removed.Calls, IsMutation);
    }

    [Fact]
    public async Task PreCancellationTouchesNoMachineBackend()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        var backend = new FakeBackend(
            InstallerMachineAssociationObservation.Missing(),
            rootsAbsent: true);
        var operations = new WindowsMachineHelperMachineOperations(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.PrepareAsync(
                request,
                new FakeReleaseLease(request, fixture.Manifest),
                InstallerMachineHelperSessionDisposition.Execute,
                cancellation.Token));

        Assert.Empty(backend.Calls);
    }

    private static FakeBackend InstalledOwnerBackend(
        InstallerMachineAssociation association) =>
        new(
            InstallerMachineAssociationObservation.Valid(association),
            rootsAbsent: false)
        {
            ServiceAssociation = association,
            ServiceInstalled = true,
            PayloadPresent = true,
            PayloadInstalled = true,
        };

    private static WindowsPayloadFixture Fixture() =>
        new(
            createPayload: false,
            removeCurrentUserCertificateOnDispose: false);

    private static bool IsMutation(string call) => call is
        "roots:create"
        or "service:stop-fence"
        or "service:configure-start"
        or "service:stop-delete"
        or "payload:stage"
        or "payload:promote"
        or "payload:remove"
        or "association:write"
        or "association:delete"
        or "roots:remove-empty";

    private static Task InvokeUninstallPathAsync(
        WindowsMachineHelperMachineOperations operations,
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        MissingProfileUninstallPath path)
    {
        var release = new FakeReleaseLease(request, manifest);
        return path switch
        {
            MissingProfileUninstallPath.PrepareExecute => operations.PrepareAsync(
                request,
                release,
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None),
            MissingProfileUninstallPath.PrepareCommittedReplay => operations.PrepareAsync(
                request,
                release,
                InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
                CancellationToken.None),
            MissingProfileUninstallPath.RemoveExecute => operations.RemoveAsync(
                request,
                release,
                InstallerMachineHelperSessionDisposition.Execute,
                CancellationToken.None),
            MissingProfileUninstallPath.RemoveCommittedReplay => operations.RemoveAsync(
                request,
                release,
                InstallerMachineHelperSessionDisposition.VerifyCommittedReplay,
                CancellationToken.None),
            MissingProfileUninstallPath.FinalVerification => operations.VerifyAsync(
                request,
                release,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, message: null),
        };
    }

    private static void AssertOrdered(
        IReadOnlyList<string> calls,
        params string[] expected)
    {
        int previous = -1;
        foreach (string call in expected)
        {
            int index = calls.IndexOf(call, previous + 1);
            Assert.True(index > previous, $"Expected '{call}' after index {previous}. Calls: {string.Join(", ", calls)}");
            previous = index;
        }
    }

    private sealed class FakeBackend : IWindowsMachineHelperMachineBackend
    {
        internal FakeBackend(
            InstallerMachineAssociationObservation association,
            bool rootsAbsent)
        {
            association.Validate();
            Association = association;
            RootsAbsent = rootsAbsent;
        }

        internal List<string> Calls { get; } = [];

        internal InstallerMachineAssociationObservation Association { get; set; }

        internal InstallerMachineAssociation? ServiceAssociation { get; set; }

        internal bool ServicePrepared { get; set; }

        internal bool ServiceInstalled { get; set; }

        internal bool PayloadPresent { get; set; }

        internal bool PayloadInstalled { get; set; }

        internal bool RootsAbsent { get; set; }

        internal int ActiveRootGuards { get; set; }

        internal WindowsMachineDeploymentPlan? LastServicePlan { get; set; }

        internal string? TargetProfileFailureDiagnostic { get; set; }

        public string ResolveTargetProfile(
            string targetSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("profile:resolve");
            Assert.Equal(TargetSid, targetSid);
            if (TargetProfileFailureDiagnostic is { } diagnosticCode)
            {
                throw new InstallerProtocolException(diagnosticCode);
            }

            return @"C:\Users\owner";
        }

        public string CreateAuthenticationToken()
        {
            Calls.Add("association:create-token");
            return FreshToken;
        }

        public WindowsMachineDeploymentPlan CreatePlan(
            InstallerRequest request,
            InstallerReleaseManifest manifest,
            InstallerMachineAssociation association,
            string targetProfileRoot,
            bool removalPlan)
        {
            Calls.Add("plan:create");
            return removalPlan
                ? WindowsMachineDeploymentPlan.CreateForRemoval(
                    request,
                    manifest,
                    association,
                    @"C:\Program Files",
                    @"C:\ProgramData",
                    targetProfileRoot)
                : WindowsMachineDeploymentPlan.Create(
                    request,
                    manifest,
                    association,
                    @"C:\Program Files",
                    @"C:\ProgramData",
                    targetProfileRoot);
        }

        public WindowsMachineDeploymentPlan CreateProfileIndependentRemovalPlan(
            InstallerRequest request,
            InstallerReleaseManifest manifest,
            InstallerMachineAssociation association)
        {
            Calls.Add("plan:create-profile-independent");
            return WindowsMachineDeploymentPlan.CreateForRemoval(
                request,
                manifest,
                association,
                @"C:\Program Files",
                @"C:\ProgramData",
                @"C:\ClashSharp.UnavailableTargetProfile");
        }

        public IWindowsMachineRootGuard CreateRootGuard(
            WindowsMachineDeploymentPlan plan,
            bool createMissing) =>
            new FakeRootGuard(this, plan.Request.TargetSid, createMissing);

        public IWindowsMachineAssociationStore CreateAssociationStore(
            WindowsMachineDeploymentPlan plan,
            IWindowsMachineRootGuard rootGuard)
        {
            Assert.NotNull(rootGuard);
            return new FakeAssociationStore(this, plan);
        }

        public bool ServiceExists(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:inspect");
            return ServiceAssociation is not null;
        }

        public bool PayloadResidueExists(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("payload:inspect");
            return PayloadPresent;
        }

        public Task StopDisableAndFenceServiceAsync(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:stop-fence");
            LastServicePlan = plan;
            if (ServiceAssociation is not null && ServiceAssociation != plan.Association)
            {
                throw new InstallerProtocolException(
                    "installer.machine.existing_service_not_owned");
            }

            if (ServiceAssociation is not null)
            {
                ServicePrepared = true;
                ServiceInstalled = false;
            }

            return Task.CompletedTask;
        }

        public Task ConfigureStartServiceAsync(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:configure-start");
            LastServicePlan = plan;
            ServiceAssociation = plan.Association;
            ServicePrepared = false;
            ServiceInstalled = true;
            return Task.CompletedTask;
        }

        public Task StopDeleteServiceAsync(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:stop-delete");
            LastServicePlan = plan;
            if (ServiceAssociation is not null && ServiceAssociation != plan.Association)
            {
                throw new InstallerProtocolException(
                    "installer.machine.existing_service_not_owned");
            }

            ServiceAssociation = null;
            ServicePrepared = false;
            ServiceInstalled = false;
            return Task.CompletedTask;
        }

        public void VerifyServicePrepared(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:verify-prepared");
            if (ServiceAssociation is not null && !ServicePrepared)
            {
                throw new InstallerProtocolException(
                    "installer.machine.service_prepare_verification_failed");
            }
        }

        public void VerifyServiceInstalled(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:verify-installed");
            if (!ServiceInstalled || ServiceAssociation != plan.Association)
            {
                throw new InstallerProtocolException(
                    "installer.machine.service_postcondition_failed");
            }
        }

        public void VerifyServiceAbsent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("service:verify-absent");
            if (ServiceAssociation is not null)
            {
                throw new InstallerProtocolException(
                    "installer.machine.service_removal_verification_failed");
            }
        }

        public Task StagePayloadAsync(
            WindowsMachineDeploymentPlan plan,
            IInstallerReleaseLease release,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(release);
            plan.Validate();
            Calls.Add("payload:stage");
            PayloadPresent = true;
            return Task.CompletedTask;
        }

        public void PromotePayload(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("payload:promote");
            PayloadPresent = true;
            PayloadInstalled = true;
        }

        public void RemovePayload(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("payload:remove");
            PayloadPresent = false;
            PayloadInstalled = false;
        }

        public void VerifyPayloadInstalled(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("payload:verify-installed");
            if (!PayloadPresent || !PayloadInstalled)
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_commit_verification_failed");
            }
        }

        public void VerifyPayloadAbsent(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("payload:verify-absent");
            if (PayloadPresent)
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_removal_verification_failed");
            }
        }

        public void RemoveEmptyRoots(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Assert.Equal(0, ActiveRootGuards);
            Assert.Null(ServiceAssociation);
            Assert.False(PayloadPresent);
            Assert.Equal(InstallerMachineAssociationStatus.Missing, Association.Status);
            Calls.Add("roots:remove-empty");
            RootsAbsent = true;
        }

        public void VerifyRootsAbsent(
            WindowsMachineDeploymentPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Validate();
            Calls.Add("roots:verify-absent");
            if (!RootsAbsent)
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_removal_verification_failed");
            }
        }

        public void VerifyProfileIndependentRemovalPostcondition(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("machine:verify-profile-independent-removed");
            if (ServiceAssociation is not null)
            {
                throw new InstallerProtocolException(
                    "installer.machine.service_removal_verification_failed");
            }

            if (PayloadPresent
                || !RootsAbsent
                || Association.Status != InstallerMachineAssociationStatus.Missing)
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_removal_verification_failed");
            }
        }

        private sealed class FakeRootGuard : IWindowsMachineRootGuard
        {
            private readonly FakeBackend _owner;
            private readonly string _targetSid;
            private readonly bool _createMissing;
            private bool _acquired;
            private bool _disposed;

            internal FakeRootGuard(
                FakeBackend owner,
                string targetSid,
                bool createMissing)
            {
                _owner = owner;
                _targetSid = targetSid;
                _createMissing = createMissing;
            }

            public Task EnsureProtectedAsync(
                WindowsMachineDeploymentPlan plan,
                CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                plan.Validate();
                if (!string.Equals(
                        plan.Request.TargetSid,
                        _targetSid,
                        StringComparison.Ordinal))
                {
                    throw new InstallerProtocolException(
                        "installer.machine.root_plan_changed");
                }

                if (!_createMissing && _owner.RootsAbsent)
                {
                    _owner.Calls.Add("roots:read-missing");
                    throw new InstallerProtocolException(
                        "installer.machine.root_verification_failed");
                }

                _owner.Calls.Add(_createMissing ? "roots:create" : "roots:read");
                if (_createMissing)
                {
                    _owner.RootsAbsent = false;
                }

                if (!_acquired)
                {
                    _owner.ActiveRootGuards++;
                    _acquired = true;
                }

                return Task.CompletedTask;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                if (_acquired)
                {
                    _owner.ActiveRootGuards--;
                }

                _disposed = true;
            }
        }

        private sealed class FakeAssociationStore : IWindowsMachineAssociationStore
        {
            private readonly FakeBackend _owner;
            private readonly WindowsMachineDeploymentPlan _plan;
            private bool _disposed;

            internal FakeAssociationStore(
                FakeBackend owner,
                WindowsMachineDeploymentPlan plan)
            {
                _owner = owner;
                _plan = plan;
            }

            public Task<InstallerMachineAssociationObservation> InspectAsync(
                CancellationToken cancellationToken)
            {
                RequireOpen(cancellationToken);
                _owner.Calls.Add("association:inspect");
                return Task.FromResult(_owner.Association);
            }

            public Task WriteAndVerifyAsync(
                InstallerMachineAssociation association,
                CancellationToken cancellationToken)
            {
                RequireOpen(cancellationToken);
                Assert.Equal(_plan.Association, association);
                if (_owner.Association.Association == association)
                {
                    return Task.CompletedTask;
                }

                if (!_plan.Request.AllowReassociation
                    && _owner.Association.Status != InstallerMachineAssociationStatus.Missing)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.association_conflict");
                }

                _owner.Calls.Add("association:write");
                _owner.Association = InstallerMachineAssociationObservation.Valid(association);
                return Task.CompletedTask;
            }

            public Task DeleteAndVerifyAsync(CancellationToken cancellationToken)
            {
                RequireOpen(cancellationToken);
                if (_owner.Association.Status == InstallerMachineAssociationStatus.Missing)
                {
                    return Task.CompletedTask;
                }

                if (_owner.Association.Association != _plan.Association)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.association_conflict");
                }

                _owner.Calls.Add("association:delete");
                _owner.Association = InstallerMachineAssociationObservation.Missing();
                return Task.CompletedTask;
            }

            public Task VerifyExactAsync(CancellationToken cancellationToken)
            {
                RequireOpen(cancellationToken);
                _owner.Calls.Add("association:verify-exact");
                if (_owner.Association.Association != _plan.Association)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.association_verification_failed");
                }

                return Task.CompletedTask;
            }

            public Task VerifyAbsentAsync(CancellationToken cancellationToken)
            {
                RequireOpen(cancellationToken);
                _owner.Calls.Add("association:verify-absent");
                if (_owner.Association.Status != InstallerMachineAssociationStatus.Missing)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.association_removal_verification_failed");
                }

                return Task.CompletedTask;
            }

            public void Dispose() => _disposed = true;

            private void RequireOpen(CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class FakeReleaseLease : IInstallerReleaseLease
    {
        internal FakeReleaseLease(
            InstallerRequest request,
            InstallerReleaseManifest manifest)
        {
            Manifest = manifest;
            bool payloadAvailable = request.Operation != InstallerOperation.Uninstall;
            Release = manifest.CreateVerifiedRelease(payloadAvailable, payloadAvailable);
        }

        public VerifiedInstallerRelease Release { get; }

        public InstallerReleaseManifest Manifest { get; }

        public IReadOnlyList<IInstallerLockedPayloadFile> LockedFiles { get; } = [];

        public Task ReverifyAsync(
            InstallerRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class WindowsMachineOperationTestListExtensions
{
    internal static int IndexOf(
        this IReadOnlyList<string> values,
        string value,
        int startIndex)
    {
        for (int index = startIndex; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
