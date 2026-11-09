using System.ComponentModel;
using System.Diagnostics;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Packages;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Retirement;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Fault-mapping universes of the two adapters that translate Win32, HRESULT and exception classes into
/// stable installer results: the elevation launcher (for all three helper bootstraps) and the user package
/// adapter. Each failure class is enumerated; a class that is not explicitly mapped must take the generic
/// stable code, keep its cause, and fatal failures must never be translated.
/// </summary>
public sealed class WindowsElevationAndPackageFaultMappingTests
{
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const int PackageAlreadyExists = unchecked((int)0x80073CFB);

    public static TheoryData<string> LauncherKinds => ["machine", "owner-transfer", "retired-uninstall"];

    public static TheoryData<int> Win32Codes =>
    [
        2, 3, 5, 15, 32, 33, 50, 87, 112, 123, 193, 206, 267, 740, 1155, 1222, 1224, 1450, 1460, 1726, 8, 14,
    ];

    public static TheoryData<int> PackageHResults =>
    [
        unchecked((int)0x80073D02), unchecked((int)0x80073CF9), unchecked((int)0x80073CF3), unchecked((int)0x80073CF0),
        unchecked((int)0x80073CFF), unchecked((int)0x80073D05), unchecked((int)0x80070005), unchecked((int)0x80070020),
        unchecked((int)0x80070070), unchecked((int)0x800700CE), unchecked((int)0x80070032), unchecked((int)0x8007000E),
        unchecked((int)0x80004005), unchecked((int)0x80131500), unchecked((int)0x80070002), unchecked((int)0x80070003),
        unchecked((int)0x800704C7), unchecked((int)0x80072EE7), 0, -1, int.MinValue, int.MaxValue,
    ];

    [Theory]
    [MemberData(nameof(LauncherKinds))]
    public async Task ExplicitElevationRefusalIsADistinctSanitizedOutcomeForEveryHelperKind(string kind)
    {
        var launcher = new WindowsRunAsProcessLauncher(static _ => throw new Win32Exception(1223, "secret shell text"));

        InstallerUserCancelledException cancelled = await Assert.ThrowsAsync<InstallerUserCancelledException>(
            () => Start(launcher, kind, Executable(), CancellationToken.None));

        Assert.Equal("installer.elevation.user_cancelled", cancelled.DiagnosticCode);
        Assert.DoesNotContain("secret", cancelled.Message, StringComparison.Ordinal);
        Assert.Null(cancelled.InnerException);
    }

    [Theory]
    [MemberData(nameof(Win32Codes))]
    public async Task EveryOtherWin32LaunchErrorIsTheGenericLaunchFailureWithItsCauseForEveryHelperKind(int code)
    {
        foreach (string kind in new[] { "machine", "owner-transfer", "retired-uninstall" })
        {
            var cause = new Win32Exception(code, "secret shell text");
            var launcher = new WindowsRunAsProcessLauncher(_ => throw cause);

            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
                () => Start(launcher, kind, Executable(), CancellationToken.None));

            Assert.Equal("installer.elevation.launch_failed", failure.DiagnosticCode);
            Assert.Same(cause, failure.InnerException);
            Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryNonWin32ExceptionClassIsTheGenericLaunchFailureAndNeverUserCancellation()
    {
        Exception[] causes =
        [
            new IOException("sharing", unchecked((int)0x80070020)),
            new IOException("disk full", unchecked((int)0x80070070)),
            new PathTooLongException("long"),
            new FileNotFoundException("missing"),
            new DirectoryNotFoundException("missing"),
            new UnauthorizedAccessException("denied"),
            new InvalidOperationException("state"),
            new PlatformNotSupportedException("platform"),
            new NotSupportedException("not supported"),
            new TimeoutException("timeout"),
            new ObjectDisposedException("process"),
            new OperationCanceledException("not the caller's token"),
            new InvalidDataException("plain", new Win32Exception(1223)),
            new InvalidOperationException("wrapped refusal", new Win32Exception(1223)),
            new AggregateException(new Win32Exception(1223)),
        ];
        foreach (Exception cause in causes)
        {
            var launcher = new WindowsRunAsProcessLauncher(_ => throw cause);

            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
                () => launcher.StartAsync(Executable(), Machine(), CancellationToken.None));

            Assert.Equal("installer.elevation.launch_failed", failure.DiagnosticCode);
            Assert.Same(cause, failure.InnerException);
        }
    }

    [Theory]
    [MemberData(nameof(LauncherKinds))]
    public async Task StableProtocolFailuresPassThroughAndFatalFailuresAreNeverTranslated(string kind)
    {
        var protocol = new InstallerProtocolException("installer.elevation.process_missing");
        Assert.Same(
            protocol,
            await Record.ExceptionAsync(() => Start(
                new WindowsRunAsProcessLauncher(_ => throw protocol),
                kind,
                Executable(),
                CancellationToken.None)));

        Assert.Equal(
            "installer.elevation.process_missing",
            Assert.IsType<InstallerProtocolException>(await Record.ExceptionAsync(() => Start(
                new WindowsRunAsProcessLauncher(static _ => null),
                kind,
                Executable(),
                CancellationToken.None))).DiagnosticCode);

        var fatal = new FatalTestException("sentinel");
        Assert.Same(
            fatal,
            await Record.ExceptionAsync(() => Start(
                new WindowsRunAsProcessLauncher(_ => throw fatal),
                kind,
                Executable(),
                CancellationToken.None)));
    }

    [Theory]
    [MemberData(nameof(LauncherKinds))]
    public async Task EveryHelperKindLaunchesOnAnStaThreadWithItsOwnExactArgumentsAndCallerCancellationIsPropagated(string kind)
    {
        ApartmentState apartment = ApartmentState.Unknown;
        ProcessStartInfo? captured = null;
        var launcher = new WindowsRunAsProcessLauncher(startInfo =>
        {
            captured = startInfo;
            apartment = Thread.CurrentThread.GetApartmentState();
            return new Process();
        });

        using (IWindowsElevatedHelperProcess process = await Start(launcher, kind, Executable(), CancellationToken.None))
        {
            Assert.NotNull(process);
        }

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal("runas", captured!.Verb);
        Assert.True(captured.UseShellExecute);
        string[] expected = Arguments(kind);
        Assert.Equal(expected.Length, captured.ArgumentList.Count);
        Assert.Equal(expected[0], captured.ArgumentList[0]);

        using var cancellation = new CancellationTokenSource();
        var cancelling = new WindowsRunAsProcessLauncher(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(cancelling, kind, Executable(), cancellation.Token));
    }

    [Fact]
    public async Task OnlyTheCanonicalSetupExecutableInAnyLocalDirectorySpellingIsLaunchedAndAlwaysInItsNormalizedForm()
    {
        string[] directories =
        [
            Path.GetTempPath(),
            @"C:\Users\Jane Doe\AppData\Local\Temp\",
            @"C:\Users\张三 O'Brien (work)\Downloads\Clash# 安装",
            @"D:\Users\محمد\Desktop\x",
            @"Z:\" + new string('d', 200) + @"\" + new string('e', 200),
            @"c:\Program Files (x86)\ClashSharp",
        ];
        foreach (string directory in directories)
        {
            foreach (string name in new[] { InstallerArtifactNames.PublishedExecutable, InstallerArtifactNames.PublishedExecutable.ToUpperInvariant() })
            {
                string path = Path.Combine(directory, name);
                ProcessStartInfo? captured = null;
                var launcher = new WindowsRunAsProcessLauncher(startInfo =>
                {
                    captured = startInfo;
                    return new Process();
                });

                using IWindowsElevatedHelperProcess process = await launcher.StartAsync(path, Machine(), CancellationToken.None);

                Assert.Equal(Path.GetFullPath(path), captured!.FileName);
            }
        }

        string[] rejected =
        [
            @"Setup.exe", @"C:Setup.exe", @"\Setup.exe", @"\\server\share\Setup.exe", @"\\?\C:\x\Setup.exe", @"\\.\C:\x\Setup.exe",
            @"C:\x\Setup.exe:stream", @"C:\x\Setup.exe\", @"C:\x\Setup.exe.exe", @"C:\x\xSetup.exe", @"C:\x\Setup.EXE.bak",
            @"C:\x\Setup", @"C:\x\", @"C:\",
        ];
        foreach (string path in rejected)
        {
            int calls = 0;
            var launcher = new WindowsRunAsProcessLauncher(_ =>
            {
                calls++;
                return new Process();
            });

            Exception? failure = await Record.ExceptionAsync(() => launcher.StartAsync(path, Machine(), CancellationToken.None));

            Assert.True(
                failure is InstallerProtocolException { DiagnosticCode: "installer.elevation.executable_path_invalid" },
                $"'{path}' was launched or failed differently: {failure?.GetType().Name ?? "success"}.");
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task ConcurrentLaunchesAreIndependentAndOnlyTheCancelledCallIsCancelled()
    {
        using var gate = new ManualResetEventSlim();
        var launcher = new WindowsRunAsProcessLauncher(startInfo =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return new Process();
        });
        Task<IWindowsElevatedHelperProcess>[] launches = Enumerable.Range(0, 8)
            .Select(_ => launcher.StartAsync(Executable(), Machine(), CancellationToken.None))
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.StartAsync(Executable(), Machine(), cancellation.Token));
        gate.Set();
        IWindowsElevatedHelperProcess[] processes = await Task.WhenAll(launches);

        Assert.Equal(8, processes.Distinct().Count());
        foreach (IWindowsElevatedHelperProcess process in processes)
        {
            process.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(PackageHResults))]
    public async Task EveryDeploymentFailureClassMapsToTheGenericCodeAndOnlyTheExactConflictHResultIsAContentConflict(int hresult)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        foreach (Func<int, Exception> create in new Func<int, Exception>[]
        {
            static code => new IOException("failure", code),
            static code => new TestComException("failure", code),
            static code => new Win32Exception(code),
        })
        {
            Exception cause = create(hresult);
            var facade = new PackageFacade { DeployFailure = static (_, failure) => failure, Failure = cause };
            var adapter = new WindowsCurrentUserPackageStoreAdapter(facade, () => request.TargetSid, _ => throw new InvalidOperationException("must not initialize"));

            InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(
                () => adapter.DeployAsync(request, lease, CancellationToken.None));

            string expected = cause.HResult == PackageAlreadyExists
                ? "installer.package.content_conflict"
                : "installer.package.deployment_failed";
            Assert.Equal(expected, failure.DiagnosticCode);
            Assert.Same(cause, failure.InnerException);
            // The one tolerated conflict retries once without dependencies, never more.
            Assert.Equal(cause.HResult == PackageAlreadyExists ? 2 : 1, facade.Deployments.Count);
            if (cause.HResult == PackageAlreadyExists)
            {
                Assert.Empty(facade.Deployments[1].DependencyPackageUris);
                Assert.NotEmpty(facade.Deployments[0].DependencyPackageUris);
            }
        }
    }

    [Fact]
    public async Task DependencyConflictIsResolvedOnlyWhenTheRetryWithoutDependenciesSucceeds()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request();
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        int initialized = 0;
        var conflict = new IOException("conflict", PackageAlreadyExists);

        var resolved = new PackageFacade
        {
            DeployFailure = static (call, failure) => call == 0 ? failure : null,
            Failure = conflict,
        };
        await new WindowsCurrentUserPackageStoreAdapter(resolved, () => request.TargetSid, _ => initialized++)
            .DeployAsync(request, lease, CancellationToken.None);
        Assert.Equal(2, resolved.Deployments.Count);
        Assert.Equal(1, initialized);

        var other = new IOException("denied", unchecked((int)0x80070005));
        var secondFails = new PackageFacade
        {
            DeployFailure = (call, failure) => call == 0 ? failure : other,
            Failure = conflict,
        };
        InstallerProtocolException failed = await Assert.ThrowsAsync<InstallerProtocolException>(
            () => new WindowsCurrentUserPackageStoreAdapter(secondFails, () => request.TargetSid, _ => initialized++)
                .DeployAsync(request, lease, CancellationToken.None));
        Assert.Equal("installer.package.deployment_failed", failed.DiagnosticCode);
        Assert.Same(other, failed.InnerException);
        Assert.Equal(1, initialized);
    }

    [Theory]
    [MemberData(nameof(PackageHResults))]
    public async Task EveryRemovalFailureClassIsTheGenericRemovalFailureWithItsCause(int hresult)
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request(InstallerOperation.Uninstall);
        await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
        var cause = new IOException("failure", hresult);
        var facade = new PackageFacade { RemoveFailure = cause };

        InstallerProtocolException failure = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsCurrentUserPackageStoreAdapter(facade, () => request.TargetSid)
                .RemoveAsync(request, lease, Installed(fixture), CancellationToken.None));

        Assert.Equal("installer.package.removal_failed", failure.DiagnosticCode);
        Assert.Same(cause, failure.InnerException);
        Assert.Equal(1, facade.Removals);
    }

    [Fact]
    public async Task FatalFailureIsNeverTranslatedAtAnyPackageBoundaryAndInspectionFailureIsSanitized()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest install = fixture.Request();
        await using WindowsInstallerReleaseLease installLease = fixture.Lock(install);
        var fatal = new FatalTestException("sentinel");

        Assert.Same(fatal, await Record.ExceptionAsync(() =>
            new WindowsCurrentUserPackageStoreAdapter(new PackageFacade { DeployFailure = static (_, failure) => failure, Failure = fatal }, () => install.TargetSid)
                .DeployAsync(install, installLease, CancellationToken.None)));
        Assert.Same(fatal, await Record.ExceptionAsync(() =>
            new WindowsCurrentUserPackageStoreAdapter(new PackageFacade { InspectFailure = fatal }, () => install.TargetSid)
                .InspectAsync(install, installLease, CancellationToken.None)));

        var cause = new Win32Exception(5);
        InstallerProtocolException inspection = await Assert.ThrowsAsync<InstallerProtocolException>(() =>
            new WindowsCurrentUserPackageStoreAdapter(new PackageFacade { InspectFailure = cause }, () => install.TargetSid)
                .InspectAsync(install, installLease, CancellationToken.None));
        Assert.Equal("installer.package.inspection_failed", inspection.DiagnosticCode);
        Assert.Same(cause, inspection.InnerException);

        InstallerRequest uninstall = fixture.Request(InstallerOperation.Uninstall);
        await using WindowsInstallerReleaseLease uninstallLease = fixture.Lock(uninstall);
        Assert.Same(fatal, await Record.ExceptionAsync(() =>
            new WindowsCurrentUserPackageStoreAdapter(new PackageFacade { RemoveFailure = fatal }, () => uninstall.TargetSid)
                .RemoveAsync(uninstall, uninstallLease, Installed(fixture), CancellationToken.None)));
    }

    [Fact]
    public async Task OperationMismatchAndTargetUserMismatchFailBeforeAnyPackageManagerCall()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        foreach (InstallerOperation operation in Enum.GetValues<InstallerOperation>())
        {
            InstallerRequest request = fixture.Request(operation);
            await using WindowsInstallerReleaseLease lease = fixture.Lock(request);
            var facade = new PackageFacade();
            var adapter = new WindowsCurrentUserPackageStoreAdapter(facade, () => request.TargetSid);
            var otherUser = new WindowsCurrentUserPackageStoreAdapter(facade, () => "S-1-5-21-9-9-9-1001");

            string? deployCode = operation == InstallerOperation.Uninstall ? "installer.package.operation_invalid" : null;
            string? removeCode = operation == InstallerOperation.Uninstall ? null : "installer.package.operation_invalid";
            Exception? deploy = await Record.ExceptionAsync(() => adapter.DeployAsync(request, lease, CancellationToken.None));
            Exception? remove = await Record.ExceptionAsync(() => adapter.RemoveAsync(request, lease, Installed(fixture), CancellationToken.None));

            Assert.Equal(deployCode, (deploy as InstallerProtocolException)?.DiagnosticCode);
            Assert.Equal(removeCode, (remove as InstallerProtocolException)?.DiagnosticCode);
            foreach (Func<Task> call in new Func<Task>[]
            {
                () => otherUser.DeployAsync(request, lease, CancellationToken.None),
                () => otherUser.RemoveAsync(request, lease, Installed(fixture), CancellationToken.None),
                () => otherUser.InspectAsync(request, lease, CancellationToken.None),
            })
            {
                InstallerProtocolException mismatch = await Assert.ThrowsAsync<InstallerProtocolException>(call);
                Assert.Equal("installer.package.target_user_mismatch", mismatch.DiagnosticCode);
            }
        }
    }

    private static Task<IWindowsElevatedHelperProcess> Start(
        WindowsRunAsProcessLauncher launcher,
        string kind,
        string executable,
        CancellationToken cancellationToken) => kind switch
        {
            "machine" => launcher.StartAsync(executable, Machine(), cancellationToken),
            "owner-transfer" => ((IWindowsOwnerTransferProcessLauncher)launcher).StartAsync(
                executable,
                InstallerOwnerTransferBootstrap.Create(Sha, 4242),
                cancellationToken),
            _ => ((IWindowsRetiredUninstallProcessLauncher)launcher).StartAsync(
                executable,
                InstallerRetiredUninstallBootstrap.Create(Sha, 4242),
                cancellationToken),
        };

    private static string[] Arguments(string kind) => kind switch
    {
        "machine" => [.. Machine().ToArguments()],
        "owner-transfer" => [.. InstallerOwnerTransferBootstrap.Create(Sha, 4242).ToArguments().Select(Normalize)],
        _ => [.. InstallerRetiredUninstallBootstrap.Create(Sha, 4242).ToArguments().Select(Normalize)],
    };

    // Session nonces are random per bootstrap; only their position and shape are comparable.
    private static string Normalize(string argument) => argument.Length == 64 && argument.All(Uri.IsHexDigit)
        ? "<nonce>"
        : argument;

    private static string Executable() => Path.Combine(Path.GetTempPath(), InstallerArtifactNames.PublishedExecutable);

    private static InstallerMachineHelperBootstrap Machine()
    {
        InstallerRequest request = new(
            InstallerOperation.Install,
            "S-1-5-21-100-200-300-1001",
            AllowReassociation: false,
            "1.2.3.4",
            Sha);
        InstallerMachineHelperInvocation invocation = InstallerMachineHelperInvocation.Create(
            InstallerMachineHelperVerb.Prepare,
            InstallerTransactionSnapshot.Create(InstallerTransactionJournal.Create(request)));
        return InstallerMachineHelperBootstrap.Create(invocation, parentProcessId: 4242);
    }

    private static InstallerInstalledPackage Installed(WindowsPayloadFixture fixture)
    {
        var identity = fixture.Manifest.PackageIdentity;
        return new InstallerInstalledPackage(
            identity.Name,
            identity.Publisher,
            identity.PublisherId,
            fixture.Manifest.ExpectedPackageVersion,
            identity.Architecture,
            identity.ResourceId,
            identity.PackageFullName,
            identity.PackageFamilyName,
            true);
    }

    private sealed class PackageFacade : IWindowsPackageManagerFacade
    {
        internal Exception? Failure { get; init; }

        internal Func<int, Exception?, Exception?>? DeployFailure { get; init; }

        internal Exception? RemoveFailure { get; init; }

        internal Exception? InspectFailure { get; init; }

        internal List<WindowsPackageDeploymentRequest> Deployments { get; } = [];

        internal int Removals { get; private set; }

        public IReadOnlyList<WindowsPackageRegistration> FindPackagesForUser(string userSecurityId, string packageFamilyName) =>
            InspectFailure is null ? [] : throw InspectFailure;

        public Task DeployAsync(WindowsPackageDeploymentRequest request)
        {
            int call = Deployments.Count;
            Deployments.Add(request);
            Exception? failure = DeployFailure?.Invoke(call, Failure);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task RemoveAsync(string packageFullName)
        {
            Removals++;
            return RemoveFailure is null ? Task.CompletedTask : Task.FromException(RemoveFailure);
        }
    }
}
