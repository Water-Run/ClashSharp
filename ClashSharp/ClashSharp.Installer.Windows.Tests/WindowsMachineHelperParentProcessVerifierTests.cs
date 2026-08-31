using System.ComponentModel;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperParentProcessVerifierTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public void CurrentProcessCanBePinnedAndReverifiedReadOnly()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process path is unavailable.");
        var verifier = new WindowsMachineHelperParentProcessVerifier();

        using IWindowsMachineHelperParentProcessLease lease = verifier.Acquire(
            Environment.ProcessId,
            executablePath);

        Assert.Equal(Environment.ProcessId, lease.ProcessId);
        using WindowsIdentity current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        Assert.Equal(current.User?.Value, lease.UserSid);
        lease.VerifyAlive();
    }

    [Fact]
    public void DifferentParentImageFailsClosedAndReleasesHandle()
    {
        string expected = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.exe");
        var native = new FakeParentNative
        {
            ImagePath = Path.Combine(Path.GetTempPath(), "different.exe"),
            Alive = true,
        };
        var verifier = new WindowsMachineHelperParentProcessVerifier(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.Acquire(4242, expected));

        Assert.Equal("installer.machine_helper.parent_image_mismatch", exception.DiagnosticCode);
        Assert.True(native.LastHandle?.IsClosed);
    }

    [Fact]
    public void ExitedPinnedParentIsRejected()
    {
        string expected = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.exe");
        var native = new FakeParentNative
        {
            ImagePath = expected,
            Alive = false,
        };
        var verifier = new WindowsMachineHelperParentProcessVerifier(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.Acquire(4242, expected));

        Assert.Equal("installer.machine_helper.parent_process_exited", exception.DiagnosticCode);
        Assert.True(native.LastHandle?.IsClosed);
    }

    [Fact]
    public void NativeOpenFailureIsSanitized()
    {
        string expected = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.exe");
        var native = new FakeParentNative
        {
            Failure = new Win32Exception(5, "sensitive path"),
        };
        var verifier = new WindowsMachineHelperParentProcessVerifier(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.Acquire(4242, expected));

        Assert.Equal(
            "installer.machine_helper.parent_process_open_failed",
            exception.DiagnosticCode);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidParentUserSidFailsClosedAndReleasesHandle()
    {
        string expected = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Installer.exe");
        var native = new FakeParentNative
        {
            ImagePath = expected,
            UserSid = "not-a-sid",
            Alive = true,
        };
        var verifier = new WindowsMachineHelperParentProcessVerifier(native);

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            verifier.Acquire(4242, expected));

        Assert.Equal("installer.request.target_sid_invalid", exception.DiagnosticCode);
        Assert.True(native.LastHandle?.IsClosed);
    }

    private sealed class FakeParentNative : IWindowsMachineHelperParentProcessNative
    {
        internal string ImagePath { get; init; } = string.Empty;

        internal bool Alive { get; init; }

        internal string UserSid { get; init; } = TargetSid;

        internal Exception? Failure { get; init; }

        internal SafeProcessHandle? LastHandle { get; private set; }

        public SafeProcessHandle Open(int processId)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            LastHandle = new SafeProcessHandle(new nint(1), ownsHandle: false);
            return LastHandle;
        }

        public string QueryImagePath(SafeProcessHandle process) => ImagePath;

        public string QueryUserSid(SafeProcessHandle process) => UserSid;

        public bool IsAlive(SafeProcessHandle process) => Alive;
    }
}
