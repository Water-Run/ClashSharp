using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsTargetProfileResolverTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Theory]
    [InlineData(@"C:\Users\owner", @"C:\Users\owner")]
    [InlineData(@"%SystemDrive%\Users\owner", @"C:\Users\owner")]
    [InlineData(@"%systemdrive%\Users\owner", @"C:\Users\owner")]
    public void ResolvesOnlyCanonicalAbsoluteOrSystemDriveProfilePaths(
        string registryValue,
        string expected)
    {
        var native = new FakeProfileNative(registryValue, @"C:\");
        var resolver = new WindowsTargetProfileResolver(native);

        string actual = resolver.Resolve(TargetSid, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(TargetSid, native.TargetSid);
        Assert.Equal(expected, native.VerifiedPath);
        Assert.Equal(1, native.VerifyCalls);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\owner")]
    [InlineData(@"\\?\C:\Users\owner")]
    [InlineData(@"C:/Users/owner")]
    [InlineData(@"C:\Users\owner\..\Windows")]
    [InlineData(@"C:\Users\owner:stream")]
    [InlineData(@"%USERPROFILE%")]
    [InlineData(@"%SystemDrive%")]
    [InlineData(@"%SystemDrive%Other\owner")]
    [InlineData(@"C:\")]
    public void RejectsUntrustedOrNonCanonicalProfilePaths(string registryValue)
    {
        var native = new FakeProfileNative(registryValue, @"C:\");
        var resolver = new WindowsTargetProfileResolver(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            resolver.Resolve(TargetSid, CancellationToken.None));

        Assert.Equal("installer.machine.target_profile_path_invalid", exception.DiagnosticCode);
        Assert.Equal(0, native.VerifyCalls);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    public void SystemDriveSourceMustBeTheExactLocalDriveRoot(string systemDrive)
    {
        var native = new FakeProfileNative(@"C:\Users\owner", systemDrive);
        var resolver = new WindowsTargetProfileResolver(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            resolver.Resolve(TargetSid, CancellationToken.None));

        Assert.Equal("installer.machine.system_drive_invalid", exception.DiagnosticCode);
        Assert.Equal(0, native.VerifyCalls);
    }

    [Fact]
    public void RegistryOrFilesystemFailureIsSanitized()
    {
        var readFailed = new WindowsTargetProfileResolver(
            new FakeProfileNative(@"C:\Users\owner", @"C:\")
            {
                ReadFailure = new IOException("registry unavailable"),
            });
        var verifyFailed = new WindowsTargetProfileResolver(
            new FakeProfileNative(@"C:\Users\owner", @"C:\")
            {
                VerifyFailure = new IOException("reparse or missing"),
            });

        InstallerProtocolException read = Assert.Throws<InstallerProtocolException>(() =>
            readFailed.Resolve(TargetSid, CancellationToken.None));
        InstallerProtocolException verify = Assert.Throws<InstallerProtocolException>(() =>
            verifyFailed.Resolve(TargetSid, CancellationToken.None));

        Assert.Equal(
            "installer.machine.target_profile_resolution_failed",
            read.DiagnosticCode);
        Assert.Equal(
            "installer.machine.target_profile_resolution_failed",
            verify.DiagnosticCode);
    }

    [Fact]
    public void PreCancellationPerformsNoRegistryOrFilesystemCalls()
    {
        var native = new FakeProfileNative(@"C:\Users\owner", @"C:\");
        var resolver = new WindowsTargetProfileResolver(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            resolver.Resolve(TargetSid, cancellation.Token));

        Assert.Equal(0, native.ReadCalls);
        Assert.Equal(0, native.VerifyCalls);
    }

    private sealed class FakeProfileNative : IWindowsTargetProfileNative
    {
        private readonly string _profilePath;
        private readonly string _systemDrive;

        internal FakeProfileNative(string profilePath, string systemDrive)
        {
            _profilePath = profilePath;
            _systemDrive = systemDrive;
        }

        internal Exception? ReadFailure { get; init; }

        internal Exception? VerifyFailure { get; init; }

        internal int ReadCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        internal string? TargetSid { get; private set; }

        internal string? VerifiedPath { get; private set; }

        public string ReadProfileImagePath(string targetSid)
        {
            ReadCalls++;
            TargetSid = targetSid;
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return _profilePath;
        }

        public string GetSystemDriveRoot() => _systemDrive;

        public void VerifyOrdinaryDirectory(string path)
        {
            VerifyCalls++;
            VerifiedPath = path;
            if (VerifyFailure is not null)
            {
                throw VerifyFailure;
            }
        }
    }
}
