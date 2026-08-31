using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Files;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachinePayloadTreeVerifierTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void MissingKnownSlotIsReportedWithoutCreatingIt()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var verifier = new WindowsMachinePayloadTreeVerifier();

        WindowsMachinePayloadTreeStatus status = verifier.Inspect(
            plan,
            plan.StagingRoot,
            CancellationToken.None);

        Assert.Equal(WindowsMachinePayloadTreeStatus.Missing, status);
        Assert.False(Directory.Exists(plan.StagingRoot));
    }

    [Fact]
    public async Task ExactExtractedTreePassesIndependentReadOnlyVerification()
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await WriteExactTreeAsync(fixture, request, plan, plan.StagingRoot);
        var verifier = new WindowsMachinePayloadTreeVerifier();

        WindowsMachinePayloadTreeStatus status = verifier.Inspect(
            plan,
            plan.StagingRoot,
            CancellationToken.None);

        Assert.Equal(WindowsMachinePayloadTreeStatus.ExactMatch, status);
        verifier.VerifyExact(plan, plan.StagingRoot, CancellationToken.None);
    }

    [Theory]
    [InlineData("extra-file")]
    [InlineData("missing-file")]
    [InlineData("changed-file")]
    [InlineData("extra-directory")]
    public async Task AnyTreeShapeOrContentDriftIsInvalid(string drift)
    {
        using var fixture = Fixture();
        InstallerRequest request = fixture.Request(targetSid: TargetSid);
        WindowsMachineDeploymentPlan plan = Plan(fixture, request);
        await WriteExactTreeAsync(fixture, request, plan, plan.StagingRoot);
        string first = Path.Combine(
            plan.StagingRoot,
            plan.PayloadTargets[0].RelativeTargetPath);
        switch (drift)
        {
            case "extra-file":
                await File.WriteAllBytesAsync(
                    Path.Combine(plan.StagingRoot, "unexpected.bin"),
                    [0x01]);
                break;
            case "missing-file":
                File.Delete(first);
                break;
            case "changed-file":
                await File.WriteAllBytesAsync(first, [0x01]);
                break;
            case "extra-directory":
                Directory.CreateDirectory(Path.Combine(plan.StagingRoot, "Unexpected"));
                break;
            default:
                throw new InvalidOperationException("Unknown test drift.");
        }

        var verifier = new WindowsMachinePayloadTreeVerifier();

        Assert.Equal(
            WindowsMachinePayloadTreeStatus.Invalid,
            verifier.Inspect(plan, plan.StagingRoot, CancellationToken.None));
        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.VerifyExact(plan, plan.StagingRoot, CancellationToken.None));
        Assert.Equal(
            "installer.machine.payload_tree_verification_failed",
            exception.DiagnosticCode);
    }

    [Fact]
    public void ArbitraryRootCannotBeInspected()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        var verifier = new WindowsMachinePayloadTreeVerifier();
        string arbitrary = Path.Combine(fixture.RootDirectory, "arbitrary");

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.Inspect(plan, arbitrary, CancellationToken.None));

        Assert.Equal("installer.machine.payload_slot_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void PresentTreeCannotSatisfyRemovalVerification()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        Directory.CreateDirectory(plan.PreviousRoot);
        var verifier = new WindowsMachinePayloadTreeVerifier();

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.VerifyAbsent(plan, plan.PreviousRoot, CancellationToken.None));

        Assert.Equal("installer.machine.payload_tree_removal_failed", exception.DiagnosticCode);
    }

    [Fact]
    public void PreCancellationOccursBeforeFilesystemInspection()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan plan = Plan(fixture);
        Directory.CreateDirectory(plan.StagingRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var verifier = new WindowsMachinePayloadTreeVerifier();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            verifier.Inspect(plan, plan.StagingRoot, cancellation.Token));
    }

    private static async Task WriteExactTreeAsync(
        WindowsPayloadFixture fixture,
        InstallerRequest request,
        WindowsMachineDeploymentPlan plan,
        string root)
    {
        Directory.CreateDirectory(root);
        await using WindowsInstallerReleaseLease release = fixture.Lock(request);
        await using WindowsMachinePayloadArchive archive =
            WindowsMachinePayloadArchive.Open(plan, release, CancellationToken.None);
        foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
        {
            string path = Path.Combine(root, target.RelativeTargetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await archive.CopyToAsync(target, destination, CancellationToken.None);
            destination.Flush(flushToDisk: true);
        }
    }

    private static WindowsPayloadFixture Fixture() =>
        new(removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Plan(
        WindowsPayloadFixture fixture,
        InstallerRequest? request = null) =>
        WindowsMachineDeploymentPlan.Create(
            request ?? fixture.Request(targetSid: TargetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            Path.Combine(fixture.RootDirectory, "Program Files"),
            Path.Combine(fixture.RootDirectory, "ProgramData"),
            Path.Combine(fixture.RootDirectory, "Users", "owner"));
}
