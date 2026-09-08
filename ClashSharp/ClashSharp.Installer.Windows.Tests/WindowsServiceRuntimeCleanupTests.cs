using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsServiceRuntimeCleanupTests
{
    [Fact]
    public void RemovesOnlyExactAssociationRuntimeIncludingReadOnlyGenerations()
    {
        using var fixture = new Fixture();
        string nested = Path.Combine(fixture.RuntimeRoot, "runtime", "effective");
        Directory.CreateDirectory(nested);
        string generation = Path.Combine(nested, "generation.yaml");
        File.WriteAllText(generation, "owned scratch data");
        File.SetAttributes(generation, FileAttributes.ReadOnly);
        string unrelated = Path.Combine(fixture.Plan.ServiceDataRoot, "unrelated.txt");
        File.WriteAllText(unrelated, "preserve");

        new WindowsServiceRuntimeCleanup().RemoveAndVerify(fixture.Plan, CancellationToken.None);
        new WindowsServiceRuntimeCleanup().RemoveAndVerify(fixture.Plan, CancellationToken.None);

        Assert.False(Directory.Exists(fixture.RuntimeRoot));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact]
    public void RuntimePathOccupiedByFileIsPreserved()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.RuntimeRoot, "unexpected file");

        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            new WindowsServiceRuntimeCleanup().RemoveAndVerify(fixture.Plan, CancellationToken.None));

        Assert.Equal("installer.machine.service_runtime_cleanup_failed", failure.DiagnosticCode);
        Assert.Equal("unexpected file", File.ReadAllText(fixture.RuntimeRoot));
    }

    [Fact]
    public void OversizedTreeIsRejectedBeforeAnyFileIsDeleted()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.RuntimeRoot);
        for (int index = 0; index <= WindowsServiceRuntimeCleanup.MaximumEntries; index++)
        {
            File.WriteAllText(Path.Combine(fixture.RuntimeRoot, $"entry-{index}"), string.Empty);
        }

        Assert.Throws<InstallerProtocolException>(() =>
            new WindowsServiceRuntimeCleanup().RemoveAndVerify(fixture.Plan, CancellationToken.None));

        Assert.Equal(WindowsServiceRuntimeCleanup.MaximumEntries + 1,
            Directory.EnumerateFiles(fixture.RuntimeRoot).Count());
    }

    [Fact]
    public void PreCancellationPreservesRuntime()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.RuntimeRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new WindowsServiceRuntimeCleanup().RemoveAndVerify(fixture.Plan, cancellation.Token));

        Assert.True(Directory.Exists(fixture.RuntimeRoot));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WindowsPayloadFixture _payload = new(
            createPayload: false, removeCurrentUserCertificateOnDispose: false);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "clashsharp-runtime-cleanup-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            const string targetSid = "S-1-5-21-100-200-300-1001";
            Plan = WindowsMachineDeploymentPlan.Create(
                _payload.Request(targetSid: targetSid), _payload.Manifest,
                InstallerMachineAssociation.Create(targetSid, new string('a', 64)),
                Path.Combine(_root, "ProgramFiles"), Path.Combine(_root, "ProgramData"), Path.Combine(_root, "Owner"));
            Directory.CreateDirectory(Plan.ServiceDataRoot);
            RuntimeRoot = Path.Combine(Plan.ServiceDataRoot, Plan.Association.BuildServicePipeName());
        }

        internal WindowsMachineDeploymentPlan Plan { get; }
        internal string RuntimeRoot { get; }

        public void Dispose()
        {
            _payload.Dispose();
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
    }
}
