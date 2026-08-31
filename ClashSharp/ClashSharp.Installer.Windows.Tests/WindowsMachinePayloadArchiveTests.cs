using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachinePayloadArchiveTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task CopiesExactlyTheSevenManifestBoundMachineFiles()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        await using WindowsMachinePayloadArchive archive =
            WindowsMachinePayloadArchive.Open(plan, release, CancellationToken.None);

        var observed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
        {
            await using var destination = new MemoryStream();
            await archive.CopyToAsync(target, destination, CancellationToken.None);
            byte[] bytes = destination.ToArray();
            observed.Add(target.RelativeTargetPath, bytes);

            Assert.Equal(target.Source.Length, bytes.LongLength);
            Assert.Equal(
                target.Source.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        Assert.Equal(7, observed.Count);
        Assert.Contains("mihomo.exe", observed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            @"Host\ClashSharp.MihomoService.exe",
            observed.Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, observed.Keys.Count(static path =>
            path.StartsWith("GeoData", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ForeignTargetCannotSelectAnArchiveEntryOrDestination()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        await using WindowsMachinePayloadArchive archive =
            WindowsMachinePayloadArchive.Open(plan, release, CancellationToken.None);
        WindowsMachinePayloadTarget foreign = plan.PayloadTargets[0] with
        {
            DestinationPath = Path.Combine(plan.CurrentRoot, "foreign.bin"),
        };
        await using var destination = new MemoryStream();

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                archive.CopyToAsync(foreign, destination, CancellationToken.None));

        Assert.Equal("installer.machine.payload_target_invalid", exception.DiagnosticCode);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task DestinationMustBeAnEmptySeekableWritableStream()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        await using WindowsMachinePayloadArchive archive =
            WindowsMachinePayloadArchive.Open(plan, release, CancellationToken.None);
        await using var destination = new MemoryStream([0x01]);

        InstallerProtocolException exception =
            await Assert.ThrowsAsync<InstallerProtocolException>(() =>
                archive.CopyToAsync(
                    plan.PayloadTargets[0],
                    destination,
                    CancellationToken.None));

        Assert.Equal("installer.machine.payload_destination_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReleaseRequestBindingIsRecheckedBeforeArchiveUse()
    {
        using var fixture = Fixture();
        InstallerRequest lockedRequest = fixture.Request(targetSid: TargetSid);
        InstallerRequest changedRequest = lockedRequest with
        {
            Operation = InstallerOperation.Repair,
        };
        WindowsMachineDeploymentPlan plan = Plan(fixture, changedRequest);
        await using WindowsInstallerReleaseLease release = fixture.Lock(lockedRequest);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachinePayloadArchive.Open(
                plan,
                release,
                CancellationToken.None));

        Assert.Equal("installer.release.request_changed", exception.DiagnosticCode);
    }

    [Fact]
    public async Task CompleteEmbeddedManifestIdentityIsRecheckedBeforeArchiveUse()
    {
        using var lockedFixture = Fixture();
        using var otherFixture = Fixture();
        InstallerRequest request = lockedFixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(otherFixture, request);
        await using WindowsInstallerReleaseLease release = lockedFixture.Lock(request);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachinePayloadArchive.Open(
                plan,
                release,
                CancellationToken.None));

        Assert.Equal("installer.release.identity_mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public async Task PreCancellationMakesNoDestinationWrites()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            WindowsMachinePayloadArchive.Open(plan, release, cancellation.Token));

        await using WindowsMachinePayloadArchive archive =
            WindowsMachinePayloadArchive.Open(plan, release, CancellationToken.None);
        await using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            archive.CopyToAsync(
                plan.PayloadTargets[0],
                destination,
                cancellation.Token));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task DisposedArchiveCannotBeReused()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        WindowsMachinePayloadArchive archive = WindowsMachinePayloadArchive.Open(
            plan,
            release,
            CancellationToken.None);
        await archive.DisposeAsync();
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            archive.CopyToAsync(
                plan.PayloadTargets[0],
                destination,
                CancellationToken.None));
    }

    private static WindowsPayloadFixture Fixture() =>
        new(removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(
        WindowsPayloadFixture fixture,
        InstallerRequest request) =>
        WindowsMachineDeploymentPlan.Create(
            request,
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", "owner"));
}
