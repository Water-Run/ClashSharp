using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Location and cleanup contracts of the payload lock over real extraction folders: an installer unpacked under a
/// profile or download path with spaces, non-ASCII text, deep nesting or different casing locks exactly the manifest
/// payload, keeps it locked until release, and a refused payload never leaks a handle that would block cleanup.
/// </summary>
public sealed class WindowsInstallerPayloadLocationContractTests : IDisposable
{
    private readonly string _base;

    public WindowsInstallerPayloadLocationContractTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashSharp.Installer.Windows.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _base = Path.GetFullPath(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    [Fact]
    public async Task EveryCorpusLocationLocksTheExactPayloadAndHoldsItUntilRelease()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request();
        int locations = 0;
        foreach (string leaf in WindowsPathCorpus.ProfileLeaves.Where(static leaf => leaf.Length < 150))
        {
            string root = Path.Combine(_base, leaf, "payload");
            Copy(fixture.PayloadRoot, root);
            string primary = Path.Combine(root, "clashsharp_1.2.3.4_x64.msix");

            await using (WindowsInstallerReleaseLease lease = WindowsInstallerPayloadLocker.Lock(request, fixture.Manifest, root, CancellationToken.None))
            {
                Assert.Equal(fixture.Manifest.Files.Count, lease.LockedFiles.Count);
                Assert.ThrowsAny<IOException>(() => File.Move(primary, primary + ".moved"));
                Assert.ThrowsAny<IOException>(() => File.Delete(primary));
                Assert.ThrowsAny<IOException>(() => Directory.Move(root, root + "-moved"));
            }

            File.Move(primary, primary + ".moved");
            File.Move(primary + ".moved", primary);
            locations++;
        }

        Assert.True(locations > 25);
    }

    [Fact]
    public async Task FileAndDirectoryNameCaseAndATrailingSeparatorDoNotChangeWhichPayloadIsLocked()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        string root = Path.Combine(_base, "Case Test", "payload");
        Copy(fixture.PayloadRoot, root);
        string upper = Path.Combine(_base, "Case Test", "PAYLOAD-UPPER");
        Directory.CreateDirectory(Path.Combine(upper, "DEPENDENCIES", "X64"));
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).ToUpperInvariant();
            File.Copy(file, Path.Combine(upper, relative));
        }

        foreach (string candidate in new[] { upper, upper + @"\", root + @"\" })
        {
            await using WindowsInstallerReleaseLease lease = WindowsInstallerPayloadLocker.Lock(
                fixture.Request(),
                fixture.Manifest,
                candidate,
                CancellationToken.None);

            Assert.Equal(fixture.Manifest.Files.Count, lease.LockedFiles.Count);
        }
    }

    [Fact]
    public async Task EveryRefusedPayloadShapeLeaksNoHandleThatWouldBlockCleanupOrRename()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        InstallerRequest request = fixture.Request();
        var mutations = new (string Label, Action<string> Apply)[]
        {
            ("extra root file", static root => File.WriteAllText(Path.Combine(root, "extra.txt"), "x")),
            ("extra nested file", static root => File.WriteAllText(Path.Combine(root, "dependencies", "extra.txt"), "x")),
            ("extra directory", static root => Directory.CreateDirectory(Path.Combine(root, "extra"))),
            ("extra empty nested directory", static root => Directory.CreateDirectory(Path.Combine(root, "dependencies", "arm64"))),
            ("missing certificate", static root => File.Delete(Path.Combine(root, "clashsharp_temporarykey.cer"))),
            ("missing dependency", static root => File.Delete(Path.Combine(root, "dependencies", "x64", "microsoft.windowsappruntime.1.8.msix"))),
            ("modified package byte", static root => FlipFirstByte(Path.Combine(root, "clashsharp_1.2.3.4_x64.msix"))),
            ("truncated certificate", static root => File.WriteAllBytes(Path.Combine(root, "clashsharp_temporarykey.cer"), [1])),
        };
        foreach ((string label, Action<string> apply) in mutations)
        {
            string root = Path.Combine(_base, "Refused " + label.Replace(' ', '-'), "payload");
            Copy(fixture.PayloadRoot, root);
            apply(root);

            Exception? failure = Record.Exception(() => WindowsInstallerPayloadLocker.Lock(request, fixture.Manifest, root, CancellationToken.None).DisposeAsync().AsTask().GetAwaiter().GetResult());

            Assert.True(
                failure is InstallerProtocolException { DiagnosticCode: var code } && code.StartsWith("installer.release.", StringComparison.Ordinal),
                $"{label}: {failure?.GetType().Name ?? "payload was accepted"}");
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.Move(file, file + ".free");
                File.Move(file + ".free", file);
            }

            string renamed = root + "-renamed";
            Directory.Move(root, renamed);
            Directory.Move(renamed, root);
        }
    }

    [Fact]
    public async Task CancelledLockTakesNoHandleAndALaterLockSucceeds()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        string root = Path.Combine(_base, "Cancelled", "payload");
        Copy(fixture.PayloadRoot, root);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            WindowsInstallerPayloadLocker.Lock(fixture.Request(), fixture.Manifest, root, cancelled.Token));

        string primary = Path.Combine(root, "clashsharp_1.2.3.4_x64.msix");
        File.Move(primary, primary + ".free");
        File.Move(primary + ".free", primary);
        await using WindowsInstallerReleaseLease lease = WindowsInstallerPayloadLocker.Lock(
            fixture.Request(),
            fixture.Manifest,
            root,
            CancellationToken.None);
        Assert.Equal(fixture.Manifest.Files.Count, lease.LockedFiles.Count);
    }

    [Fact]
    public async Task DeeplyNestedExtractionFolderEitherLocksOrFailsClosedWithTheStableCode()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using var fixture = new WindowsPayloadFixture(removeCurrentUserCertificateOnDispose: false);
        string root = Path.Combine(_base, new string('n', 120), new string('m', 120), "payload");
        Copy(fixture.PayloadRoot, root);

        Exception? failure = null;
        try
        {
            await using WindowsInstallerReleaseLease lease = WindowsInstallerPayloadLocker.Lock(
                fixture.Request(),
                fixture.Manifest,
                root,
                CancellationToken.None);
            Assert.Equal(fixture.Manifest.Files.Count, lease.LockedFiles.Count);
        }
        catch (Exception exception) when (exception is InstallerProtocolException)
        {
            failure = exception;
        }

        // Paths beyond MAX_PATH depend on the machine's long-path policy; either outcome is acceptable,
        // a crash with a raw Win32 or IO exception is not.
        Assert.True(failure is null or InstallerProtocolException { DiagnosticCode: "installer.release.payload_lock_failed" });
        string primary = Path.Combine(root, "clashsharp_1.2.3.4_x64.msix");
        File.Move(primary, primary + ".free");
    }

    private static void FlipFirstByte(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }
}
