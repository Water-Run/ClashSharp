using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Contracts of the installer entry point's two testable pieces: the embedded release-manifest loader, which must
/// fail closed for every malformed resource layout, and the preview runtime, which must never become executable and
/// must produce readiness the presentation layer accepts for every platform the probe can report.
/// </summary>
public sealed class InstallerEntryPointRuntimeContractTests
{
    private const string ResourceName = "ClashSharp.Installer.ReleaseManifest.json";

    [Fact]
    public void ManifestResourceIsLoadedByteForByteAndValidated()
    {
        using var fixture = Fixture();
        byte[] manifest = fixture.ManifestBytes;

        EmbeddedInstallerReleaseManifest loaded = EmbeddedInstallerReleaseManifest.Load(Generated(("ClashSharp.Installer.ReleaseManifest.json", manifest)));

        Assert.Equal(manifest, loaded.Bytes.ToArray());
        Assert.Equal(fixture.Manifest.ExpectedPackageVersion, loaded.Manifest.ExpectedPackageVersion);
        Assert.Equal(fixture.Manifest.InstallerPayloadSha256, loaded.Manifest.InstallerPayloadSha256);
    }

    [Fact]
    public void EveryResourceLayoutOtherThanExactlyOneExactlyNamedManifestFailsClosed()
    {
        using var fixture = Fixture();
        byte[] manifest = fixture.ManifestBytes;
        var layouts = new (string Label, Assembly Assembly, string Code)[]
        {
            ("no resources", Generated(), "installer.release.manifest_missing"),
            ("other resource only", Generated(("Other.json", manifest)), "installer.release.manifest_missing"),
            ("wrong case only", Generated((ResourceName.ToLowerInvariant(), manifest)), "installer.release.manifest_missing"),
            ("wrong case upper only", Generated((ResourceName.ToUpperInvariant(), manifest)), "installer.release.manifest_missing"),
            ("exact plus case alias", Generated((ResourceName, manifest), (ResourceName.ToLowerInvariant(), manifest)), "installer.release.manifest_missing"),
            ("prefix only", Generated((ResourceName[..^5], manifest)), "installer.release.manifest_missing"),
            ("suffix added", Generated((ResourceName + ".bak", manifest)), "installer.release.manifest_missing"),
            ("empty resource", Generated((ResourceName, [])), "installer.release.manifest_size_invalid"),
            ("oversize resource", Generated((ResourceName, new byte[InstallerPayloadBudgets.MaximumManifestBytes + 1])), "installer.release.manifest_size_invalid"),
        };

        foreach ((string label, Assembly assembly, string code) in layouts)
        {
            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(
                () => EmbeddedInstallerReleaseManifest.Load(assembly));

            Assert.True(failure.DiagnosticCode == code, $"{label}: expected {code}, got {failure.DiagnosticCode}.");
        }
    }

    [Fact]
    public void CorruptManifestBytesAreRejectedWithTheCodecsStableCodeAtEveryTruncation()
    {
        using var fixture = Fixture();
        byte[] manifest = fixture.ManifestBytes;
        foreach (int length in new[] { 1, 2, manifest.Length / 4, manifest.Length / 2, manifest.Length - 1 })
        {
            Assembly assembly = Generated((ResourceName, manifest[..length]));

            Exception? failure = Record.Exception(() => EmbeddedInstallerReleaseManifest.Load(assembly));

            Assert.True(
                failure is InstallerProtocolException { DiagnosticCode: var code } && code.StartsWith("installer.release.", StringComparison.Ordinal),
                $"Truncation to {length} produced {failure?.GetType().Name ?? "success"}.");
        }

        foreach (byte[] garbage in new[] { "{}"u8.ToArray(), "null"u8.ToArray(), [0xEF, 0xBB, 0xBF], [0xFF, 0xFE, 0x00], new byte[16] })
        {
            Assembly assembly = Generated((ResourceName, garbage));
            Assert.Throws<InstallerProtocolException>(() => EmbeddedInstallerReleaseManifest.Load(assembly));
        }
    }

    [Fact]
    public void DefaultLoadFromAnAssemblyWithoutTheResourceFailsClosedAndNullIsRejected()
    {
        Assert.Equal(
            "installer.release.manifest_missing",
            Assert.Throws<InstallerProtocolException>(() => EmbeddedInstallerReleaseManifest.Load(typeof(InstallerEntryPointRuntimeContractTests).Assembly)).DiagnosticCode);
        Assert.Throws<ArgumentNullException>(() => EmbeddedInstallerReleaseManifest.Load(null!));
    }

    [Fact]
    public async Task PreviewRuntimeIsNeverExecutableForAnyPlatformTheProbeCanReport()
    {
        int platforms = 0;
        foreach (bool windows in new[] { true, false })
        {
            foreach (bool workstation in new[] { true, false })
            {
                foreach (int build in new[] { 0, 10240, 21999, 22000, 22621, 26100, int.MaxValue })
                {
                    foreach (InstallerCpuArchitecture os in Enum.GetValues<InstallerCpuArchitecture>())
                    {
                        foreach (InstallerCpuArchitecture process in Enum.GetValues<InstallerCpuArchitecture>())
                        {
                            foreach (bool serverDesktop in new[] { true, false })
                            {
                                var facts = new InstallerPlatformFacts(windows, workstation, build, os, process, serverDesktop);
                                var runtime = new MigrationPreviewInstallerRuntime(new FixedProbe(facts));
                                platforms++;

                                InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(CancellationToken.None);
                                var progress = new RecordingProgress();
                                InstallerExecutionResult result = await runtime.ExecuteAsync(InstallerOperation.Install, progress, CancellationToken.None);

                                Assert.False(readiness.CanExecute);
                                Assert.Empty(readiness.AllowedOperations);
                                Assert.Null(readiness.RecoveryOperation);
                                Assert.Equal(InstallerProductState.Available, readiness.ProductState);
                                Assert.Equal(InstallerExecutionOutcome.Blocked, result.Outcome);
                                Assert.Equal(readiness.DiagnosticCode, result.DiagnosticCode);
                                Assert.False(result.RecoveryPending);
                                Assert.Null(result.LastDurablePhase);
                                Assert.Empty(progress.Reports);
                                InstallerProtocolValidation.ValidateDiagnosticCode(readiness.DiagnosticCode);
                            }
                        }
                    }
                }
            }
        }

        int architectures = Enum.GetValues<InstallerCpuArchitecture>().Length;
        Assert.Equal(2 * 2 * 7 * architectures * architectures * 2, platforms);
    }

    [Fact]
    public async Task PreviewReadinessIsAcceptedByThePresentationLayerForEveryDistinctPlatformOutcome()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstallerPlatformFacts facts in new[]
        {
            new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new InstallerPlatformFacts(false, false, 0, InstallerCpuArchitecture.Unknown, InstallerCpuArchitecture.Unknown),
            new InstallerPlatformFacts(true, true, 19045, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.Arm64, InstallerCpuArchitecture.Arm64),
            new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.Arm64, InstallerCpuArchitecture.X64),
            new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X86),
            new InstallerPlatformFacts(true, false, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64),
            new InstallerPlatformFacts(true, false, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64, true),
            new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X86, InstallerCpuArchitecture.X86),
        })
        {
            using var model = new InstallerShellViewModel(new MigrationPreviewInstallerRuntime(new FixedProbe(facts)));

            await model.InitializeAsync();

            Assert.NotEqual("installer.runtime.readiness_invalid", model.DiagnosticCode);
            Assert.False(string.IsNullOrWhiteSpace(model.StatusTitle));
            Assert.False(string.IsNullOrWhiteSpace(model.StatusDetail));
            Assert.False(model.PrimaryActionCommand.CanExecute(null));
            Assert.NotEmpty(model.Capabilities);
            seen.Add(model.DiagnosticCode);
        }

        Assert.True(seen.Count >= 3, "The universe must reach several distinct platform outcomes.");
    }

    [Fact]
    public async Task PreviewRuntimeHonoursCancellationBeforeProbingAndRejectsMissingProgress()
    {
        var probe = new FixedProbe(new InstallerPlatformFacts(true, true, 26100, InstallerCpuArchitecture.X64, InstallerCpuArchitecture.X64));
        var runtime = new MigrationPreviewInstallerRuntime(probe);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.InspectReadinessAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.ExecuteAsync(InstallerOperation.Install, new RecordingProgress(), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runtime.ExecuteAsync(InstallerOperation.Install, null!, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
        Assert.Throws<ArgumentNullException>(() => new MigrationPreviewInstallerRuntime(null!));
    }

    [Fact]
    public async Task ProbeFailureIsNeverConvertedIntoAnExecutableStatus()
    {
        var failure = new InstallerProtocolException("installer.environment.version_probe_failed");
        var runtime = new MigrationPreviewInstallerRuntime(new FixedProbe(null!) { Failure = failure });

        Assert.Same(failure, await Record.ExceptionAsync(() => runtime.InspectReadinessAsync(CancellationToken.None)));
        Assert.Same(failure, await Record.ExceptionAsync(() => runtime.ExecuteAsync(
            InstallerOperation.Uninstall,
            new RecordingProgress(),
            CancellationToken.None)));
    }

    private static WindowsPayloadFixture Fixture() => new(createPayload: false, removeCurrentUserCertificateOnDispose: false);

    /// <summary>Builds a real loadable assembly whose manifest resources are exactly the ones supplied.</summary>
    private static Assembly Generated(params (string Name, byte[] Bytes)[] resources)
    {
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName("ClashSharp.Tests.Generated" + Guid.NewGuid().ToString("N")),
            typeof(object).Assembly);
        _ = builder.DefineDynamicModule("Generated");
        MetadataBuilder metadata = builder.GenerateMetadata(out BlobBuilder il, out BlobBuilder fieldData);
        var managedResources = new BlobBuilder();
        foreach ((string name, byte[] bytes) in resources)
        {
            uint offset = (uint)managedResources.Count;
            managedResources.WriteInt32(bytes.Length);
            managedResources.WriteBytes(bytes);
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(name), default, offset);
        }

        var pe = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            il,
            mappedFieldData: fieldData,
            managedResources: managedResources);
        var image = new BlobBuilder();
        pe.Serialize(image);
        using var stream = new MemoryStream(image.ToArray());
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private sealed class FixedProbe(InstallerPlatformFacts facts) : IInstallerPlatformProbe
    {
        internal Exception? Failure { get; init; }

        internal int Calls { get; private set; }

        public InstallerPlatformFacts Inspect(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? facts : throw Failure;
        }
    }

    private sealed class RecordingProgress : IProgress<InstallerProgress>
    {
        internal List<InstallerProgress> Reports { get; } = [];

        public void Report(InstallerProgress value) => Reports.Add(value);
    }
}
