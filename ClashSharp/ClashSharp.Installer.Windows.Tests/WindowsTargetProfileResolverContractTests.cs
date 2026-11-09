using System.ComponentModel;
using System.Security;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Contracts of target-profile resolution over real-world profile names and every failure class the
/// registry and file-system probes can produce: a profile path is either returned in its exact
/// canonical form or rejected with one stable code, never rewritten and never leaked as a raw exception.
/// </summary>
public sealed class WindowsTargetProfileResolverContractTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string PathInvalid = "installer.machine.target_profile_path_invalid";

    public static TheoryData<string> NativeFailures =>
    [
        nameof(IOException), nameof(UnauthorizedAccessException), nameof(SecurityException),
        nameof(ObjectDisposedException), nameof(InvalidOperationException), "Win32Exception5",
        "Win32Exception32", "Win32Exception1450", nameof(FormatException), "DiskFull", "PathTooLong",
        nameof(InvalidDataException), nameof(NotSupportedException), nameof(TimeoutException),
    ];

    [Fact]
    public void EveryCorpusProfileResolvesToItsExactCanonicalPathInEveryAcceptedSpelling()
    {
        foreach (string parent in WindowsPathCorpus.ProfileParents)
        {
            foreach (string leaf in WindowsPathCorpus.ProfileLeaves)
            {
                string expected = parent + @"\" + leaf;
                foreach (string spelling in new[] { expected, expected + @"\" })
                {
                    var native = new ProfileNative(spelling, @"C:\");
                    string actual = new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None);

                    Assert.Equal(expected, actual);
                    Assert.Equal(expected, native.VerifiedPath);
                    Assert.Equal(1, native.VerifyCalls);
                    Assert.Equal(actual, Path.GetFullPath(actual));
                    Assert.Equal(
                        actual,
                        new WindowsTargetProfileResolver(new ProfileNative(actual, @"C:\"))
                            .Resolve(TargetSid, CancellationToken.None));
                }
            }
        }
    }

    [Fact]
    public void SystemDriveSpellingExpandsOnlyThroughTheExactDriveRootInAnyCase()
    {
        foreach (string spelling in new[] { "%SystemDrive%", "%systemdrive%", "%SYSTEMDRIVE%", "%SystemDrive%" })
        {
            foreach (string drive in new[] { @"C:\", @"c:\", @"D:\", @"E:\" })
            {
                foreach (string leaf in WindowsPathCorpus.ProfileLeaves)
                {
                    var native = new ProfileNative(spelling + @"\Users\" + leaf, drive);

                    string actual = new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None);

                    Assert.Equal(drive.TrimEnd('\\') + @"\Users\" + leaf, actual);
                }
            }
        }
    }

    [Fact]
    public void EveryNonCanonicalOrReinterpretedFormIsRejectedBeforeAnyFileSystemProbe()
    {
        foreach (string raw in WindowsPathCorpus.NonCanonicalProfilePaths)
        {
            var native = new ProfileNative(raw, @"C:\");

            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None));

            Assert.True(failure.DiagnosticCode == PathInvalid, $"'{raw}' produced {failure.DiagnosticCode}");
            Assert.Equal(0, native.VerifyCalls);
        }
    }

    [Fact]
    public void SystemDriveTokenWithoutASeparatorOrFollowedByAnotherVariableIsRejected()
    {
        foreach (string raw in new[]
        {
            "%SystemDrive%", @"%SystemDrive%Users\owner", @"%SystemDrive%\Users\%USERNAME%",
            @"%SystemDrive%\%Other%", @"C:\Users\%USERNAME%", @"%SystemRoot%\ServiceProfiles\LocalService",
            @"%SystemDrive%%SystemDrive%\Users\owner", @"x%SystemDrive%\Users\owner",
        })
        {
            var native = new ProfileNative(raw, @"C:\");

            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None));

            Assert.Equal(PathInvalid, failure.DiagnosticCode);
            Assert.Equal(0, native.VerifyCalls);
        }
    }

    [Fact]
    public void ProfilePathLengthIsBoundedBeforePathNormalization()
    {
        string atLimit = @"C:\Users\" + new string('a', 32_767 - @"C:\Users\".Length);
        string overLimit = atLimit + "a";

        var over = new ProfileNative(overLimit, @"C:\");
        Assert.Equal(
            PathInvalid,
            Assert.Throws<InstallerProtocolException>(() => new WindowsTargetProfileResolver(over)
                .Resolve(TargetSid, CancellationToken.None)).DiagnosticCode);
        Assert.Equal(0, over.VerifyCalls);

        // The raw value is within the registry budget; a normalized path beyond it is a wrapped
        // failure, not an unhandled PathTooLongException.
        var boundary = new ProfileNative(atLimit, @"C:\");
        Exception? outcome = Record.Exception(() => new WindowsTargetProfileResolver(boundary)
            .Resolve(TargetSid, CancellationToken.None));
        Assert.True(
            outcome is null or InstallerProtocolException,
            $"A boundary-length profile must resolve or fail closed, got {outcome?.GetType().Name}.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-1-2-3-0x1")]
    public void InvalidOrPrivilegedTargetNeverReachesTheRegistry(string sid)
    {
        var native = new ProfileNative(@"C:\Users\owner", @"C:\");

        Assert.Throws<InstallerProtocolException>(() =>
            new WindowsTargetProfileResolver(native).Resolve(sid, CancellationToken.None));

        Assert.Equal(0, native.ReadCalls);
    }

    [Theory]
    [MemberData(nameof(NativeFailures))]
    public void EveryRegistryOrDirectoryFailureClassIsSanitizedAndKeepsItsCause(string failure)
    {
        foreach (bool atRead in new[] { true, false })
        {
            Exception cause = CreateFailure(failure);
            var native = new ProfileNative(@"C:\Users\owner", @"C:\")
            {
                ReadFailure = atRead ? cause : null,
                VerifyFailure = atRead ? null : cause,
            };

            InstallerProtocolException wrapped = Assert.Throws<InstallerProtocolException>(() =>
                new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None));

            Assert.Equal("installer.machine.target_profile_resolution_failed", wrapped.DiagnosticCode);
            Assert.Same(cause, wrapped.InnerException);
        }
    }

    [Fact]
    public void StableProtocolFailuresFatalFailuresAndCancellationPassThroughUnwrapped()
    {
        var protocol = new InstallerProtocolException("installer.machine.target_profile_missing");
        Assert.Same(protocol, Assert.Throws<InstallerProtocolException>(() => new WindowsTargetProfileResolver(
            new ProfileNative(@"C:\Users\owner", @"C:\") { ReadFailure = protocol })
            .Resolve(TargetSid, CancellationToken.None)));

        var fatal = new FatalTestException("sentinel");
        Assert.Same(fatal, Assert.Throws<FatalTestException>(() => new WindowsTargetProfileResolver(
            new ProfileNative(@"C:\Users\owner", @"C:\") { VerifyFailure = fatal })
            .Resolve(TargetSid, CancellationToken.None)));

        using var cancellation = new CancellationTokenSource();
        var native = new ProfileNative(@"C:\Users\owner", @"C:\") { OnVerify = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => new WindowsTargetProfileResolver(native)
            .Resolve(TargetSid, cancellation.Token));
        Assert.Equal(1, native.VerifyCalls);
    }

    [Fact]
    public void WrappedFatalCauseIsNeverDowngradedToAResolutionFailure()
    {
        var fatal = new FatalTestException("sentinel");
        var wrapper = new IOException("outer", fatal);

        Exception? thrown = Record.Exception(() => new WindowsTargetProfileResolver(
            new ProfileNative(@"C:\Users\owner", @"C:\") { ReadFailure = wrapper })
            .Resolve(TargetSid, CancellationToken.None));

        Assert.Same(wrapper, thrown);
    }

    [Fact]
    public void SystemDriveRootMustBeAnExactLocalDriveLetterRoot()
    {
        foreach (string drive in new[]
        {
            @"C:", @"C:\Windows", @"\\server\share\", @"\\?\C:\", @"1:\", @"CC:\", "", " ",
            @"C:\Windows\System32\", "relative\\",
        })
        {
            var native = new ProfileNative(@"C:\Users\owner", drive);

            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                new WindowsTargetProfileResolver(native).Resolve(TargetSid, CancellationToken.None));

            Assert.True(
                failure.DiagnosticCode == "installer.machine.system_drive_invalid",
                $"'{drive}' produced {failure.DiagnosticCode}");
            Assert.Equal(0, native.VerifyCalls);
        }
    }

    private static Exception CreateFailure(string kind) => kind switch
    {
        nameof(IOException) => new IOException("io"),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("denied"),
        nameof(SecurityException) => new SecurityException("security"),
        nameof(ObjectDisposedException) => new ObjectDisposedException("key"),
        nameof(InvalidOperationException) => new InvalidOperationException("state"),
        "Win32Exception5" => new Win32Exception(5),
        "Win32Exception32" => new Win32Exception(32),
        "Win32Exception1450" => new Win32Exception(1450),
        nameof(FormatException) => new FormatException("format"),
        "DiskFull" => new IOException("full", unchecked((int)0x80070070)),
        "PathTooLong" => new PathTooLongException("long"),
        nameof(InvalidDataException) => new InvalidDataException("data"),
        nameof(NotSupportedException) => new NotSupportedException("nope"),
        nameof(TimeoutException) => new TimeoutException("slow"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class ProfileNative(string profilePath, string systemDrive) : IWindowsTargetProfileNative
    {
        internal Exception? ReadFailure { get; init; }

        internal Exception? VerifyFailure { get; init; }

        internal Action? OnVerify { get; init; }

        internal int ReadCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        internal string? VerifiedPath { get; private set; }

        public string ReadProfileImagePath(string targetSid)
        {
            ReadCalls++;
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return profilePath;
        }

        public string GetSystemDriveRoot() => systemDrive;

        public void VerifyOrdinaryDirectory(string path)
        {
            VerifyCalls++;
            VerifiedPath = path;
            OnVerify?.Invoke();
            if (VerifyFailure is not null)
            {
                throw VerifyFailure;
            }
        }
    }
}
