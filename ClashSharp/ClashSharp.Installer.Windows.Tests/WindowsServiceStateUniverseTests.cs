using System.ComponentModel;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Decision-table and fault-universe contracts for the service layer: which existing service tuples may
/// be touched, which SCM failure classes reconcile to success or to an uncertain state, and that an
/// interruption at any native call leaves a state that a plain re-run converges from.
/// </summary>
public sealed class WindowsServiceStateUniverseTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string ForeignDacl = "D:(A;;GA;;;WD)";
    private static readonly string[] MachineAuthoritySids = ["S-1-5-18", "S-1-5-32-544"];

    public static TheoryData<string> ScmFailures =>
    [
        "Win32Exception5", "Win32Exception32", "Win32Exception1056", "Win32Exception1060", "Win32Exception1062",
        "Win32Exception1072", "Win32Exception1223", "Win32Exception1722", "Win32Exception1726",
        nameof(IOException), nameof(UnauthorizedAccessException), nameof(InvalidOperationException),
        nameof(TimeoutException), "SharingViolation", "DiskFull", "PathTooLong", nameof(OperationCanceledException),
    ];

    [Fact]
    public void ConfigurationEqualityComparesEveryDeclaredPropertyAndNothingIsForgottenWhenOneIsAdded()
    {
        using var fixture = Fixture();
        WindowsServiceConfiguration baseline = Plan(fixture).Service;
        Assert.True(WindowsServiceConfigurationVerifier.ConfigurationMatches(baseline, baseline with { }));
        Assert.True(WindowsServiceConfigurationVerifier.ConfigurationMatches(
            baseline,
            baseline with { Dependencies = [.. baseline.Dependencies] }));

        PropertyInfo[] properties = typeof(WindowsServiceConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.Name != "EqualityContract")
            .ToArray();
        Assert.Equal(10, properties.Length);
        foreach (PropertyInfo property in properties)
        {
            WindowsServiceConfiguration changed = With(baseline, property, Alter(property.GetValue(baseline)!));

            Assert.False(
                WindowsServiceConfigurationVerifier.ConfigurationMatches(baseline, changed),
                $"A different {property.Name} must not be treated as the same service configuration.");
            Assert.False(WindowsServiceConfigurationVerifier.ConfigurationMatches(changed, baseline));
        }
    }

    [Fact]
    public void ExpectedTupleRejectsEverySingleDeclaredDeviationAndBoundsAreExact()
    {
        using var fixture = Fixture();
        WindowsServiceConfiguration expected = Plan(fixture).Service;
        expected.ValidateExpected();
        foreach (PropertyInfo property in typeof(WindowsServiceConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.Name is not ("EqualityContract" or "BinaryPath")))
        {
            WindowsServiceConfiguration changed = With(expected, property, Alter(property.GetValue(expected)!));

            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(changed.ValidateExpected);

            Assert.Equal("installer.machine.service_configuration_invalid", failure.DiagnosticCode);
        }

        foreach (string name in new[] { "a/b", @"a\b", new string('n', 257), "", " " })
        {
            Assert.Throws<InstallerProtocolException>((expected with { ServiceName = name }).Validate);
        }

        foreach (string text in new[] { "a\nb", "a\0b", "a\tb", "a\u007Fb", "a\u0085b" })
        {
            Assert.Throws<InstallerProtocolException>((expected with { Description = text }).Validate);
            Assert.Throws<InstallerProtocolException>((expected with { BinaryPath = text }).Validate);
            Assert.Throws<InstallerProtocolException>((expected with { Dependencies = [text] }).Validate);
        }

        (expected with { Dependencies = Enumerable.Repeat("dep", 64).ToArray() }).Validate();
        Assert.Throws<InstallerProtocolException>(
            (expected with { Dependencies = Enumerable.Repeat("dep", 65).ToArray() }).Validate);
        (expected with { BinaryPath = new string('b', WindowsMachineDeploymentPlan.MaximumServiceCommandCharacters) }).Validate();
        Assert.Throws<InstallerProtocolException>((expected with
        {
            BinaryPath = new string('b', WindowsMachineDeploymentPlan.MaximumServiceCommandCharacters + 1),
        }).Validate);
        Assert.Throws<InstallerProtocolException>((expected with { Dependencies = [new string('d', 257)] }).Validate);
        Assert.Throws<InstallerProtocolException>((expected with { Dependencies = [" "] }).Validate);
    }

    [Fact]
    public void PollingLimitsAcceptExactlyTheDocumentedClosedRange()
    {
        foreach ((int polls, TimeSpan interval, bool valid) in new (int, TimeSpan, bool)[]
        {
            (1, TimeSpan.FromTicks(1), true), (3_600, TimeSpan.FromSeconds(5), true), (300, TimeSpan.FromMilliseconds(100), true),
            (0, TimeSpan.FromMilliseconds(1), false), (-1, TimeSpan.FromMilliseconds(1), false),
            (3_601, TimeSpan.FromMilliseconds(1), false), (int.MaxValue, TimeSpan.FromMilliseconds(1), false),
            (3, TimeSpan.Zero, false), (3, TimeSpan.FromTicks(-1), false), (3, TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(1), false),
            (3, TimeSpan.MaxValue, false),
        })
        {
            var limits = new WindowsServiceMutationLimits(polls, interval);
            if (valid)
            {
                limits.Validate();
                _ = new WindowsServiceMutation(new ServiceFake(null), new NoDelay(), limits);
            }
            else
            {
                Assert.Throws<ArgumentOutOfRangeException>(limits.Validate);
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    new WindowsServiceMutation(new ServiceFake(null), new NoDelay(), limits));
            }
        }
    }

    [Fact]
    public void SnapshotValidationRejectsUndefinedStatesAndMalformedDescriptorsInTheWholeUniverse()
    {
        using var fixture = Fixture();
        WindowsServiceSnapshot valid = Installed(Plan(fixture));
        foreach (WindowsServiceRuntimeState state in Enum.GetValues<WindowsServiceRuntimeState>())
        {
            (valid with { RuntimeState = state }).Validate();
        }

        foreach (uint raw in new uint[] { 0, 8, 9, 255, 0x8000_0000, uint.MaxValue })
        {
            Assert.Equal(
                "installer.machine.service_snapshot_invalid",
                Assert.Throws<InstallerProtocolException>(
                    (valid with { RuntimeState = (WindowsServiceRuntimeState)raw }).Validate).DiagnosticCode);
        }

        foreach (string? dacl in new[] { null, "", " ", "garbage", "D:(A;;GA;;;ZZ)", "D:(A;;QQ;;;SY)" })
        {
            Exception? failure = Record.Exception((valid with { DaclSddl = dacl! }).Validate);

            Assert.True(
                failure is InstallerProtocolException { DiagnosticCode: "installer.machine.service_snapshot_invalid" },
                $"'{dacl}' produced {failure?.GetType().Name ?? "no failure"}.");
        }
    }

    [Fact]
    public void PreparedVerificationAcceptsExactlyTheFencedStoppedDisabledTupleOrAnAbsentService()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        new WindowsServiceConfigurationVerifier(new ServiceFake(null)).VerifyPrepared(CancellationToken.None);
        foreach (WindowsServiceProcessType process in Enum.GetValues<WindowsServiceProcessType>())
        {
            foreach (string account in new[] { "LocalSystem", "localsystem", @"NT AUTHORITY\LocalService" })
            {
                foreach (WindowsServiceStartMode mode in Enum.GetValues<WindowsServiceStartMode>())
                {
                    foreach (WindowsServiceRuntimeState state in Enum.GetValues<WindowsServiceRuntimeState>())
                    {
                        foreach (string dacl in new[] { fence, WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid), ForeignDacl })
                        {
                            var snapshot = new WindowsServiceSnapshot(
                                plan.Service with { ProcessType = process, AccountName = account, StartMode = mode },
                                state,
                                dacl);
                            bool expected = process == WindowsServiceProcessType.OwnProcess
                                && account == "LocalSystem"
                                && mode == WindowsServiceStartMode.Disabled
                                && state == WindowsServiceRuntimeState.Stopped
                                && dacl == fence;
                            var verifier = new WindowsServiceConfigurationVerifier(new ServiceFake(snapshot));

                            Exception? outcome = Record.Exception(() => verifier.VerifyPrepared(CancellationToken.None));

                            if (expected)
                            {
                                Assert.Null(outcome);
                            }
                            else
                            {
                                Assert.Equal(
                                    "installer.machine.service_prepare_verification_failed",
                                    Assert.IsType<InstallerProtocolException>(outcome).DiagnosticCode);
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void InstalledVerificationRequiresTheExactTupleDaclAndRunningStateOnlyWhenAsked()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServiceSnapshot installed = Installed(plan);
        foreach (WindowsServiceRuntimeState state in Enum.GetValues<WindowsServiceRuntimeState>())
        {
            var verifier = new WindowsServiceConfigurationVerifier(new ServiceFake(installed with { RuntimeState = state }));

            verifier.VerifyInstalled(plan, requireRunning: false, CancellationToken.None);
            Exception? strict = Record.Exception(() => verifier.VerifyInstalled(plan, requireRunning: true, CancellationToken.None));
            if (state == WindowsServiceRuntimeState.Running)
            {
                Assert.Null(strict);
            }
            else
            {
                Assert.Equal(
                    "installer.machine.service_postcondition_failed",
                    Assert.IsType<InstallerProtocolException>(strict).DiagnosticCode);
            }
        }

        foreach (PropertyInfo property in typeof(WindowsServiceConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.Name != "EqualityContract"))
        {
            WindowsServiceConfiguration changed = With(plan.Service, property, Alter(property.GetValue(plan.Service)!));
            var verifier = new WindowsServiceConfigurationVerifier(new ServiceFake(installed with { Configuration = changed }));

            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                verifier.VerifyInstalled(plan, requireRunning: false, CancellationToken.None));

            Assert.True(
                failure.DiagnosticCode is "installer.machine.service_postcondition_failed" or "installer.machine.service_configuration_invalid",
                property.Name + " -> " + failure.DiagnosticCode);
        }

        Assert.Throws<InstallerProtocolException>(() =>
            new WindowsServiceConfigurationVerifier(new ServiceFake(installed with { DaclSddl = ForeignDacl }))
                .VerifyInstalled(plan, requireRunning: false, CancellationToken.None));
        Assert.Equal(
            "installer.machine.service_missing",
            Assert.Throws<InstallerProtocolException>(() =>
                new WindowsServiceConfigurationVerifier(new ServiceFake(null)).Inspect(CancellationToken.None)).DiagnosticCode);
        Assert.Equal(
            "installer.machine.service_removal_verification_failed",
            Assert.Throws<InstallerProtocolException>(() =>
                new WindowsServiceConfigurationVerifier(new ServiceFake(installed)).VerifyAbsent(CancellationToken.None)).DiagnosticCode);
    }

    [Theory]
    [MemberData(nameof(ScmFailures))]
    public void InspectionFailureOfAnyClassIsSanitizedAndMalformedSnapshotsAreRejected(string failure)
    {
        using var fixture = Fixture();
        Exception cause = CreateFailure(failure);
        var native = new ServiceFake(Installed(Plan(fixture))) { InspectFailure = cause };
        var verifier = new WindowsServiceConfigurationVerifier(native);

        Exception? thrown = Record.Exception(() => verifier.InspectOptional(CancellationToken.None));

        if (cause is OperationCanceledException)
        {
            Assert.Same(cause, thrown);
        }
        else
        {
            InstallerProtocolException wrapped = Assert.IsType<InstallerProtocolException>(thrown);
            Assert.Equal("installer.machine.service_inspection_failed", wrapped.DiagnosticCode);
            Assert.Same(cause, wrapped.InnerException);
        }

        var fatal = new FatalTestException("sentinel");
        Assert.Same(fatal, Record.Exception(() => new WindowsServiceConfigurationVerifier(
            new ServiceFake(null) { InspectFailure = fatal }).InspectOptional(CancellationToken.None)));
        var protocol = new InstallerProtocolException("installer.machine.service_name_invalid");
        Assert.Same(protocol, Record.Exception(() => new WindowsServiceConfigurationVerifier(
            new ServiceFake(null) { InspectFailure = protocol }).InspectOptional(CancellationToken.None)));
        Assert.Equal(
            "installer.machine.service_snapshot_invalid",
            Assert.Throws<InstallerProtocolException>(() => new WindowsServiceConfigurationVerifier(
                new ServiceFake(Installed(Plan(fixture)) with { DaclSddl = "garbage" }))
                .InspectOptional(CancellationToken.None)).DiagnosticCode);
    }

    [Fact]
    public async Task EveryExistingTupleIsEitherRejectedWithoutAnyScmWriteOrReachesTheExactTerminalState()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        string expectedDacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid);
        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        int rejected = 0;
        int accepted = 0;
        foreach (WindowsServiceProcessType process in Enum.GetValues<WindowsServiceProcessType>())
        {
            foreach (string account in new[] { "LocalSystem", "localsystem", @"NT AUTHORITY\LocalService", @".\admin" })
            {
                foreach (WindowsServiceStartMode mode in Enum.GetValues<WindowsServiceStartMode>())
                {
                    foreach (string dacl in new[] { expectedDacl, fence, ForeignDacl })
                    {
                        foreach (bool foreignBinary in new[] { false, true })
                        {
                            foreach (WindowsServiceRuntimeState state in new[]
                            {
                                WindowsServiceRuntimeState.Running, WindowsServiceRuntimeState.Stopped,
                                WindowsServiceRuntimeState.StopPending,
                            })
                            {
                                WindowsServiceConfiguration configuration = plan.Service with
                                {
                                    ProcessType = process,
                                    AccountName = account,
                                    StartMode = mode,
                                    BinaryPath = foreignBinary ? @"""C:\Other\service.exe""" : plan.Service.BinaryPath,
                                };
                                var snapshot = new WindowsServiceSnapshot(configuration, state, dacl);
                                bool safe = process == WindowsServiceProcessType.OwnProcess && account == "LocalSystem";
                                bool owned = safe && !foreignBinary
                                    && mode is WindowsServiceStartMode.Automatic or WindowsServiceStartMode.Disabled
                                    && (dacl == expectedDacl || dacl == fence);
                                string? expectedFailure = !safe
                                    ? "installer.machine.existing_service_unsafe"
                                    : owned ? null : "installer.machine.existing_service_not_owned";

                                foreach (string action in new[] { "fence", "delete" })
                                {
                                    var native = new ServiceFake(snapshot);
                                    WindowsServiceMutation mutation = Mutation(native);
                                    Task run = action == "fence"
                                        ? mutation.StopDisableAndFenceAsync(plan, CancellationToken.None)
                                        : mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None);

                                    if (expectedFailure is not null)
                                    {
                                        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() => run);
                                        Assert.Equal(expectedFailure, failure.DiagnosticCode);
                                        Assert.Equal(0, native.Mutations);
                                        rejected++;
                                    }
                                    else
                                    {
                                        await run;
                                        accepted++;
                                        if (action == "fence")
                                        {
                                            Assert.Equal(WindowsServiceRuntimeState.Stopped, native.Snapshot!.RuntimeState);
                                            Assert.Equal(WindowsServiceStartMode.Disabled, native.Snapshot.Configuration.StartMode);
                                            Assert.Equal(fence, native.Snapshot.DaclSddl);
                                        }
                                        else
                                        {
                                            Assert.Null(native.Snapshot);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(rejected > 0 && accepted > 0);
    }

    [Fact]
    public async Task ConfigureAndStartDecisionTableCoversEveryPreparedConfiguredAndForeignCombination()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        string expectedDacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid);
        string fence = WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl();
        foreach (WindowsServiceProcessType process in Enum.GetValues<WindowsServiceProcessType>())
        {
            foreach (string account in new[] { "LocalSystem", @"NT AUTHORITY\LocalService" })
            {
                foreach (WindowsServiceStartMode mode in Enum.GetValues<WindowsServiceStartMode>())
                {
                    foreach (string dacl in new[] { expectedDacl, fence, ForeignDacl })
                    {
                        foreach (WindowsServiceRuntimeState state in Enum.GetValues<WindowsServiceRuntimeState>())
                        {
                            foreach (bool drift in new[] { false, true })
                            {
                                WindowsServiceConfiguration configuration = plan.Service with
                                {
                                    ProcessType = process,
                                    AccountName = account,
                                    StartMode = mode,
                                    DisplayName = drift ? "Previous" : plan.Service.DisplayName,
                                };
                                var snapshot = new WindowsServiceSnapshot(configuration, state, dacl);
                                bool safe = process == WindowsServiceProcessType.OwnProcess && account == "LocalSystem";
                                bool configured = !drift && mode == WindowsServiceStartMode.Automatic && dacl == expectedDacl && safe;
                                bool prepared = state == WindowsServiceRuntimeState.Stopped
                                    && mode == WindowsServiceStartMode.Disabled && dacl == fence;
                                var native = new ServiceFake(snapshot);

                                Exception? outcome = await Record.ExceptionAsync(() =>
                                    Mutation(native).ConfigureStartAndVerifyAsync(plan, CancellationToken.None));

                                if (!safe)
                                {
                                    Assert.Equal(
                                        "installer.machine.existing_service_unsafe",
                                        Assert.IsType<InstallerProtocolException>(outcome).DiagnosticCode);
                                    Assert.Equal(0, native.Mutations);
                                }
                                else if (configured || prepared)
                                {
                                    Assert.Null(outcome);
                                    Assert.Equal(configured ? 0 : 1, native.EnsureCalls);
                                    Assert.Equal(WindowsServiceRuntimeState.Running, native.Snapshot!.RuntimeState);
                                    Assert.Equal(expectedDacl, native.Snapshot.DaclSddl);
                                }
                                else
                                {
                                    Assert.Equal(
                                        "installer.machine.existing_service_not_prepared",
                                        Assert.IsType<InstallerProtocolException>(outcome).DiagnosticCode);
                                    Assert.Equal(0, native.Mutations);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ScmFailures))]
    public async Task EveryScmFailureClassReconcilesToSuccessWhenTheEffectLandedAndToUncertainWhenItDid(string failure)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        foreach (string action in new[] { "fence", "configure", "start", "delete" })
        {
            foreach (bool effectLanded in new[] { true, false })
            {
                WindowsServiceSnapshot? start = action switch
                {
                    "configure" or "start" => Fenced(plan),
                    _ => Installed(plan),
                };
                var native = new ServiceFake(action == "start" ? Installed(plan) with { RuntimeState = WindowsServiceRuntimeState.Stopped } : start)
                {
                    MutationFailure = CreateFailure(failure),
                    FailAction = action,
                    ApplyFailedAction = effectLanded,
                };
                var delay = new NoDelay();
                WindowsServiceMutation mutation = Mutation(native, delay, maximumPolls: 4);
                Task run = action switch
                {
                    "fence" => mutation.StopDisableAndFenceAsync(plan, CancellationToken.None),
                    "delete" => mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None),
                    _ => mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None),
                };

                if (effectLanded)
                {
                    await run;
                    Assert.Equal(0, delay.Calls);
                }
                else
                {
                    InstallerStateUncertainException uncertain =
                        await Assert.ThrowsAsync<InstallerStateUncertainException>(() => run);
                    Assert.Equal("installer.machine.service_state_uncertain", uncertain.DiagnosticCode);
                    Assert.Equal(3, delay.Calls);
                }

                Assert.Equal(1, native.FailedActionCalls);
            }
        }
    }

    [Fact]
    public async Task FatalNativeFailureIsNeverReconciledAwayAtAnyMutationBoundary()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        foreach (string action in new[] { "fence", "configure", "start", "delete" })
        {
            var fatal = new FatalTestException("sentinel");
            WindowsServiceSnapshot seed = action switch
            {
                "configure" => Fenced(plan),
                "start" => Installed(plan) with { RuntimeState = WindowsServiceRuntimeState.Stopped },
                _ => Installed(plan),
            };
            var native = new ServiceFake(seed)
            {
                MutationFailure = fatal,
                FailAction = action,
                ApplyFailedAction = true,
            };
            WindowsServiceMutation mutation = Mutation(native);
            Task run = action switch
            {
                "fence" => mutation.StopDisableAndFenceAsync(plan, CancellationToken.None),
                "delete" => mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None),
                _ => mutation.ConfigureStartAndVerifyAsync(plan, CancellationToken.None),
            };

            Assert.Same(fatal, await Record.ExceptionAsync(() => run));
            Assert.Equal(1, native.FailedActionCalls);
        }
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public async Task ServiceMarkedForDeletionDisappearsOnlyAfterItsHandlesCloseAndIsAwaitedBoundedly(int lingeringInspections, int maximumPolls)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var native = new ServiceFake(Installed(plan))
        {
            MutationFailure = new Win32Exception(1072),
            FailAction = "delete",
            ApplyFailedAction = true,
            DeleteLingersForInspections = lingeringInspections,
        };
        var delay = new NoDelay();
        WindowsServiceMutation mutation = Mutation(native, delay, maximumPolls);

        Exception? outcome = await Record.ExceptionAsync(() => mutation.StopDeleteAndVerifyAsync(plan, CancellationToken.None));

        // The first lingering inspection is the poll that follows the mutation itself.
        if (lingeringInspections < maximumPolls)
        {
            Assert.Null(outcome);
            Assert.Equal(lingeringInspections, delay.Calls);
        }
        else
        {
            Assert.Equal(
                "installer.machine.service_state_uncertain",
                Assert.IsType<InstallerStateUncertainException>(outcome).DiagnosticCode);
            Assert.Equal(maximumPolls - 1, delay.Calls);
        }

        Assert.Equal(1, native.FailedActionCalls);
    }

    [Theory]
    [InlineData("fence")]
    [InlineData("configure")]
    [InlineData("delete")]
    public async Task InterruptionAtAnyNativeCallLeavesAStateThatAPlainRerunConvergesFrom(string action)
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        WindowsServiceSnapshot? seed = action == "configure" ? null : Installed(plan);
        ServiceFake reference = new(seed);
        await Run(Mutation(reference), plan, action, CancellationToken.None);
        int totalCalls = reference.Calls;
        Assert.True(totalCalls >= 3);

        for (int cut = 1; cut <= totalCalls + 1; cut++)
        {
            using var cancellation = new CancellationTokenSource();
            var native = new ServiceFake(seed) { CancelAtCall = cut, Cancellation = cancellation };
            WindowsServiceMutation mutation = Mutation(native);

            Exception? interrupted = await Record.ExceptionAsync(() => Run(mutation, plan, action, cancellation.Token));

            Assert.True(
                interrupted is null or OperationCanceledException,
                $"Interrupting call {cut} produced {interrupted?.GetType().Name}.");
            native.CancelAtCall = 0;
            await Run(mutation, plan, action, CancellationToken.None);

            AssertSameTerminalState(reference.Snapshot, native.Snapshot, $"{action} cut at call {cut}");
        }
    }

    [Fact]
    public void ExpectedDaclGrantsTheTargetUserQueryRightsOnlyForEveryAcceptedAccountSid()
    {
        const int dangerous = 0x2 | 0x10 | 0x20 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x20000000 | 0x40000000;
        foreach (string sid in new[]
        {
            TargetSid, "S-1-5-21-1-2-3-500", "S-1-5-21-4294967295-4294967295-4294967295-4294967295",
            "S-1-12-1-1234567890-1234567890-1234567890-1234567890",
        })
        {
            string dacl = WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(sid);
            var descriptor = new RawSecurityDescriptor(dacl);
            CommonAce[] entries = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
            CommonAce target = Assert.Single(entries, ace => ace.SecurityIdentifier.Value == sid);

            Assert.Equal(3, entries.Length);
            Assert.Equal(AceType.AccessAllowed, target.AceType);
            Assert.Equal(0, target.AccessMask & dangerous);
            Assert.NotEqual(0, target.AccessMask & 0x1);
            Assert.NotEqual(0, target.AccessMask & 0x4);
            Assert.All(
                entries.Where(ace => ace.SecurityIdentifier.Value != sid),
                ace => Assert.Contains(ace.SecurityIdentifier.Value, MachineAuthoritySids));
            Assert.Equal(dacl, WindowsServiceConfigurationVerifier.NormalizeDacl(dacl));
        }

        var fence = new RawSecurityDescriptor(WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl());
        Assert.Equal(2, fence.DiscretionaryAcl!.Count);
    }

    [Fact]
    public void TargetSidCannotInjectAdditionalAccessEntriesIntoTheServiceDescriptor()
    {
        foreach (string hostile in new[]
        {
            "S-1-5-21-1-2-3-4)(A;;GA;;;WD", "S-1-5-21-1-2-3-4;", "S-1-5-21-1-2-3-4)", "D:(A;;GA;;;WD)", "S-1-5-21-1-2-3-4 ",
            "S-1-5-21-1-2-3-4\n", "WD", "S-1-1-0", "S-1-5-18", "S-1-5-32-544", "S-1-5-21-1-2-3-4)(A;;GA;;;S-1-1-0",
        })
        {
            Exception? failure = Record.Exception(() => WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(hostile));

            Assert.True(failure is InstallerProtocolException, $"'{hostile}' was accepted as a target SID.");
        }
    }

    [Fact]
    public void ServiceDescriptorBuilderRefusesGroupSidsAsTheTargetAccount()
    {
        foreach (string group in new[] { "S-1-5-11", "S-1-5-32-545", "S-1-5-32-546", "S-1-5-4", "S-1-5-19", "S-1-5-20", "S-1-1-1" })
        {
            Assert.Throws<InstallerProtocolException>(() => WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(group));
        }
    }

    [Fact]
    public void RealServiceNativeRefusesEveryNameButItsOwnBeforeAnyServiceControlManagerCall()
    {
        // Only names of services that cannot exist: a missing gate would then fail this test
        // through a missing exception instead of acting on a real service.
        foreach (string name in new[]
        {
            string.Empty, " ", "clashsharpmihomo", "ClashSharpMihomo ", "ClashSharpMihomo\0", "ClashSharpMihomo2",
            "ClashSharpMihomo.NonExistent." + Guid.NewGuid().ToString("N"), "NoSuchService" + Guid.NewGuid().ToString("N"),
        })
        {
            foreach (Action action in new Action[]
            {
                () => WindowsServiceMutationNative.Instance.StopDisableAndFence(name, "D:"),
                () => WindowsServiceMutationNative.Instance.Start(name),
                () => WindowsServiceMutationNative.Instance.StopAndDelete(name),
                () => _ = WindowsServiceMutationNative.Instance.Inspect(name),
                () => _ = WindowsServiceConfigurationNative.Instance.Inspect(name),
            })
            {
                InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(action);

                Assert.Equal("installer.machine.service_name_invalid", failure.DiagnosticCode);
            }
        }
    }

    private static async Task Run(WindowsServiceMutation mutation, WindowsMachineDeploymentPlan plan, string action, CancellationToken token)
    {
        switch (action)
        {
            case "fence":
                await mutation.StopDisableAndFenceAsync(plan, token);
                break;
            case "configure":
                await mutation.ConfigureStartAndVerifyAsync(plan, token);
                break;
            default:
                await mutation.StopDeleteAndVerifyAsync(plan, token);
                break;
        }
    }

    private static void AssertSameTerminalState(WindowsServiceSnapshot? expected, WindowsServiceSnapshot? actual, string label)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null || actual is null)
        {
            return;
        }

        Assert.True(
            WindowsServiceConfigurationVerifier.ConfigurationMatches(expected.Configuration, actual.Configuration),
            label);
        Assert.Equal(expected.RuntimeState, actual.RuntimeState);
        Assert.Equal(expected.DaclSddl, actual.DaclSddl);
    }

    private static WindowsServiceConfiguration With(WindowsServiceConfiguration source, PropertyInfo property, object value)
    {
        ConstructorInfo constructor = typeof(WindowsServiceConfiguration).GetConstructors()
            .Single(static candidate => candidate.GetParameters().Length == 10);
        object?[] arguments = constructor.GetParameters()
            .Select(parameter => string.Equals(parameter.Name, property.Name, StringComparison.Ordinal)
                ? value
                : typeof(WindowsServiceConfiguration).GetProperty(parameter.Name!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(source))
            .ToArray();
        return (WindowsServiceConfiguration)constructor.Invoke(arguments);
    }

    private static object Alter(object value) => value switch
    {
        string text => text + "x",
        bool flag => !flag,
        IReadOnlyList<string> list => (IReadOnlyList<string>)[.. list, "Tcpip"],
        Enum enumeration => NextEnumValue(enumeration),
        _ => throw new NotSupportedException(value.GetType().Name),
    };

    private static object NextEnumValue(Enum value)
    {
        Array values = Enum.GetValues(value.GetType());
        int index = Array.IndexOf(values, value);
        return values.GetValue((index + 1) % values.Length)!;
    }

    private static Exception CreateFailure(string kind) => kind switch
    {
        nameof(IOException) => new IOException("io"),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("denied"),
        nameof(InvalidOperationException) => new InvalidOperationException("state"),
        nameof(TimeoutException) => new TimeoutException("slow"),
        nameof(OperationCanceledException) => new OperationCanceledException("not the caller"),
        "SharingViolation" => new IOException("busy", unchecked((int)0x80070020)),
        "DiskFull" => new IOException("full", unchecked((int)0x80070070)),
        "PathTooLong" => new PathTooLongException("long"),
        _ when kind.StartsWith("Win32Exception", StringComparison.Ordinal) =>
            new Win32Exception(int.Parse(kind["Win32Exception".Length..], System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(WindowsPayloadFixture fixture) =>
        WindowsMachineDeploymentPlan.Create(
            fixture.Request(targetSid: TargetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            @"C:\Program Files",
            @"C:\ProgramData",
            @"C:\Users\owner");

    private static WindowsServiceSnapshot Installed(WindowsMachineDeploymentPlan plan) => new(
        plan.Service,
        WindowsServiceRuntimeState.Running,
        WindowsServiceConfigurationVerifier.BuildExpectedDaclSddl(TargetSid));

    private static WindowsServiceSnapshot Fenced(WindowsMachineDeploymentPlan plan) => new(
        plan.Service with { StartMode = WindowsServiceStartMode.Disabled },
        WindowsServiceRuntimeState.Stopped,
        WindowsServiceConfigurationVerifier.BuildMutationFenceDaclSddl());

    private static WindowsServiceMutation Mutation(ServiceFake native, NoDelay? delay = null, int maximumPolls = 3) =>
        new(native, delay ?? new NoDelay(), new WindowsServiceMutationLimits(maximumPolls, TimeSpan.FromMilliseconds(1)));

    private sealed class NoDelay : IWindowsServiceMutationDelay
    {
        internal int Calls { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>SCM stand-in whose mutations are atomic and whose failures can follow or precede their effect.</summary>
    private sealed class ServiceFake(WindowsServiceSnapshot? snapshot) : IWindowsServiceMutationNative
    {
        private int _lingering;

        internal WindowsServiceSnapshot? Snapshot { get; private set; } = snapshot;

        internal Exception? InspectFailure { get; init; }

        internal Exception? MutationFailure { get; init; }

        internal string? FailAction { get; init; }

        internal bool ApplyFailedAction { get; init; } = true;

        internal int DeleteLingersForInspections { get; init; }

        internal int CancelAtCall { get; set; }

        internal CancellationTokenSource? Cancellation { get; init; }

        internal int Calls { get; private set; }

        internal int Mutations { get; private set; }

        internal int EnsureCalls { get; private set; }

        internal int FailedActionCalls { get; private set; }

        public WindowsServiceSnapshot? Inspect(string serviceName)
        {
            Tick();
            if (InspectFailure is not null)
            {
                throw InspectFailure;
            }

            if (_lingering > 0)
            {
                _lingering--;
                return _lingeringSnapshot;
            }

            return Snapshot;
        }

        public void StopDisableAndFence(string serviceName, string fenceDaclSddl)
        {
            Mutate("fence", () =>
            {
                if (Snapshot is not null)
                {
                    Snapshot = Snapshot with
                    {
                        Configuration = Snapshot.Configuration with { StartMode = WindowsServiceStartMode.Disabled },
                        RuntimeState = WindowsServiceRuntimeState.Stopped,
                        DaclSddl = fenceDaclSddl,
                    };
                }
            });
        }

        public void EnsureConfigured(WindowsServiceConfiguration configuration, string expectedDaclSddl)
        {
            EnsureCalls++;
            Mutate("configure", () => Snapshot = new WindowsServiceSnapshot(
                configuration,
                WindowsServiceRuntimeState.Stopped,
                expectedDaclSddl));
        }

        public void Start(string serviceName)
        {
            Mutate("start", () =>
            {
                if (Snapshot is not null)
                {
                    Snapshot = Snapshot with { RuntimeState = WindowsServiceRuntimeState.Running };
                }
            });
        }

        public void StopAndDelete(string serviceName)
        {
            Mutate("delete", () =>
            {
                _lingeringSnapshot = Snapshot is null ? null : Snapshot with { RuntimeState = WindowsServiceRuntimeState.Stopped };
                _lingering = DeleteLingersForInspections;
                Snapshot = null;
            });
        }

        private WindowsServiceSnapshot? _lingeringSnapshot;

        private void Mutate(string action, Action effect)
        {
            Tick();
            Mutations++;
            bool failing = MutationFailure is not null && string.Equals(FailAction, action, StringComparison.Ordinal);
            if (failing)
            {
                FailedActionCalls++;
            }

            if (!failing || ApplyFailedAction)
            {
                effect();
            }

            if (failing)
            {
                throw MutationFailure!;
            }
        }

        private void Tick()
        {
            Calls++;
            if (Calls == CancelAtCall)
            {
                Cancellation!.Cancel();
            }
        }
    }
}
