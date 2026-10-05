using System.IO.Compression;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMaintenancePayloadStateReaderTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void ExactOriginalPackageAndDeployedFilesProduceIndependentFingerprintsAndReleaseHandles()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);

        IReadOnlyList<WindowsMaintenanceFileFingerprint> fingerprints = new WindowsMaintenancePayloadStateReader()
            .Read(plan, package, CancellationToken.None);

        Assert.Equal(7, fingerprints.Count);
        for (int index = 0; index < fingerprints.Count; index++)
        {
            Assert.Equal(plan.PayloadTargets[index].Source.Sha256, fingerprints[index].Sha256);
            Assert.Equal(plan.PayloadTargets[index].Source.Length, fingerprints[index].Length);
            using var exclusive = new FileStream(plan.PayloadTargets[index].DestinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Directory.Move(plan.CurrentRoot, plan.PreviousRoot);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("extra-file")]
    [InlineData("extra-folder")]
    public void AnyDeployedContentOrTreeShapeChangeCannotBecomeOriginalEvidence(string scenario)
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        string first = plan.PayloadTargets[0].DestinationPath;
        if (scenario == "changed") { File.WriteAllBytes(first, "changed"u8.ToArray()); }
        else if (scenario == "missing") { File.Delete(first); }
        else if (scenario == "extra-file") { File.WriteAllText(Path.Combine(plan.CurrentRoot, "unexpected.txt"), "preserve"); }
        else { Directory.CreateDirectory(Path.Combine(plan.CurrentRoot, "unexpected")); }

        Assert.Throws<InstallerProtocolException>(() => new WindowsMaintenancePayloadStateReader().Read(plan, package, CancellationToken.None));

        Directory.Move(plan.CurrentRoot, plan.PreviousRoot);
    }

    [Fact]
    public void EqualLengthChangedBytesAreDetectedAndTheOriginalSourceRemainsUnmodified()
    {
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        (WindowsMachineDeploymentPlan plan, string package) = Prepare(fixture);
        string first = plan.PayloadTargets[0].DestinationPath;
        byte[] bytes = File.ReadAllBytes(first);
        bytes[0] ^= 1;
        File.WriteAllBytes(first, bytes);
        string original = Path.Combine(package, plan.PayloadTargets[0].Source.Path.Replace('/', Path.DirectorySeparatorChar));

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsMaintenancePayloadStateReader().Read(plan, package, CancellationToken.None));

        Assert.Equal("installer.recovery.original_payload_changed", failure.DiagnosticCode);
        using var sourceExclusive = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var targetExclusive = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static (WindowsMachineDeploymentPlan Plan, string Package) Prepare(WindowsPayloadFixture fixture)
    {
        string package = Path.Combine(fixture.RootDirectory, "registered");
        ZipFile.ExtractToDirectory(fixture.PrimaryPath, package);
        WindowsMachineDeploymentPlan plan = WindowsMachineDeploymentPlan.Create(fixture.Request(InstallerOperation.Repair, Owner),
            fixture.Manifest, InstallerMachineAssociation.Create(Owner, Token), Path.Combine(fixture.RootDirectory, "ProgramFiles"),
            Path.Combine(fixture.RootDirectory, "ProgramData"), Path.Combine(fixture.RootDirectory, "profile"));
        foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.DestinationPath)!);
            string source = Path.Combine(package, target.Source.Path.Replace('/', Path.DirectorySeparatorChar));
            File.Copy(source, target.DestinationPath);
        }
        return (plan, package);
    }
}
