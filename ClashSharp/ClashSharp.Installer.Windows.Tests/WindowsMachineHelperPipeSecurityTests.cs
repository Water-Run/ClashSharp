using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsMachineHelperPipeSecurityTests
{
    private const string LogonSid = "S-1-5-5-123-456";

    [Fact]
    public void PipeDaclSupportsSplitTokenAndOverTheShoulderElevationWithoutBroadReadAccess()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        SecurityIdentifier logonSid = new(LogonSid);

        PipeSecurity security = WindowsMachineHelperPipeSecurity.Create(logonSid);
        PipeAccessRule[] rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(4, rules.Length);
        AssertExactRule(
            rules,
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny);
        AssertExactRule(
            rules,
            logonSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow);
        AssertExactRule(
            rules,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow);
        AssertExactRule(
            rules,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow);
        Assert.DoesNotContain(rules, static rule =>
            rule.IdentityReference is SecurityIdentifier sid
            && (sid.IsWellKnown(WellKnownSidType.WorldSid)
                || sid.IsWellKnown(WellKnownSidType.AnonymousSid)
                || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
                || sid.IsWellKnown(WellKnownSidType.InteractiveSid)));
    }

    [Theory]
    [InlineData("S-1-5-21-100-200-300-1001")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-18")]
    public void NonLogonIdentityCannotBroadenThePipeAcl(string sidText)
    {
        WindowsPayloadFixture.AssertWindows11X64();

        InstallerProtocolException exception = Assert.Throws<InstallerProtocolException>(() =>
            WindowsMachineHelperPipeSecurity.Create(new SecurityIdentifier(sidText)));

        Assert.Equal(
            "installer.machine_helper.logon_identity_invalid",
            exception.DiagnosticCode);
    }

    [Fact]
    public void CurrentTokenHasOneCanonicalLogonSid()
    {
        WindowsPayloadFixture.AssertWindows11X64();

        SecurityIdentifier logonSid =
            WindowsMachineHelperPipeSecurity.GetCurrentLogonSid();

        Assert.True(logonSid.IsWellKnown(WellKnownSidType.LogonIdsSid));
        Assert.StartsWith("S-1-5-5-", logonSid.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinglePipeInstanceBindsBothEndpointProcessIds()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        InstallerMachineHelperBootstrap bootstrap = Bootstrap();
        SecurityIdentifier logonSid =
            WindowsMachineHelperPipeSecurity.GetCurrentLogonSid();
        await using NamedPipeServerStream server =
            WindowsMachineHelperPipeSecurity.CreateServerStream(bootstrap, logonSid);

        Exception? duplicateFailure = Record.Exception(() =>
        {
            using NamedPipeServerStream duplicate =
                WindowsMachineHelperPipeSecurity.CreateServerStream(bootstrap, logonSid);
        });
        Assert.True(
            duplicateFailure is IOException or UnauthorizedAccessException,
            $"Unexpected duplicate-pipe outcome: {duplicateFailure?.GetType().Name ?? "none"}.");

        await using IWindowsMachineHelperClient client =
            new WindowsMachineHelperClientFactory().Create(bootstrap);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task accepting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepting;

        var identity = new WindowsMachineHelperPipeIdentity();
        identity.VerifyClient(server.SafePipeHandle, Environment.ProcessId);
        client.VerifyServer(Environment.ProcessId);
    }

    [Fact]
    public void PeerPidPolicyMatchesBothDirectionsAndRejectsMismatch()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakePipeIdentityNative
        {
            ClientProcessId = 101,
            ServerProcessId = 202,
        };
        var identity = new WindowsMachineHelperPipeIdentity(native);
        using SafePipeHandle pipe = new(new nint(1), ownsHandle: false);

        identity.VerifyClient(pipe, 101);
        identity.VerifyServer(pipe, 202);
        InstallerProtocolException clientMismatch =
            Assert.Throws<InstallerProtocolException>(() =>
                identity.VerifyClient(pipe, 202));
        InstallerProtocolException serverMismatch =
            Assert.Throws<InstallerProtocolException>(() =>
                identity.VerifyServer(pipe, 101));

        Assert.Equal(
            "installer.machine_helper.pipe_peer_identity_invalid",
            clientMismatch.DiagnosticCode);
        Assert.Equal(
            "installer.machine_helper.pipe_peer_identity_invalid",
            serverMismatch.DiagnosticCode);
        Assert.Equal(2, native.ClientQueries);
        Assert.Equal(2, native.ServerQueries);
    }

    [Fact]
    public void InvalidHandleOrPidFailsBeforeNativeQuery()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        var native = new FakePipeIdentityNative();
        var identity = new WindowsMachineHelperPipeIdentity(native);
        using SafePipeHandle invalid = new(nint.Zero, ownsHandle: false);
        using SafePipeHandle valid = new(new nint(1), ownsHandle: false);

        Assert.Throws<InstallerProtocolException>(() =>
            identity.VerifyClient(invalid, 1));
        Assert.Throws<InstallerProtocolException>(() =>
            identity.VerifyServer(valid, 0));

        Assert.Equal(0, native.ClientQueries);
        Assert.Equal(0, native.ServerQueries);
    }

    [Fact]
    public void NativeQueryFailureIsSanitizedButFatalFailurePropagates()
    {
        WindowsPayloadFixture.AssertWindows11X64();
        using SafePipeHandle pipe = new(new nint(1), ownsHandle: false);
        var recoverable = new WindowsMachineHelperPipeIdentity(
            new FakePipeIdentityNative { Failure = new IOException("sensitive pipe") });

        InstallerProtocolException sanitized =
            Assert.Throws<InstallerProtocolException>(() =>
                recoverable.VerifyClient(pipe, 101));

        Assert.Equal(
            "installer.machine_helper.pipe_peer_query_failed",
            sanitized.DiagnosticCode);
        Assert.DoesNotContain("sensitive", sanitized.Message, StringComparison.Ordinal);

        var fatal = new WindowsMachineHelperPipeIdentity(
            new FakePipeIdentityNative { Failure = new FatalTestException("sentinel") });
        Assert.Throws<FatalTestException>(() => fatal.VerifyServer(pipe, 202));
    }

    private static InstallerMachineHelperBootstrap Bootstrap()
    {
        byte[] transactionSeed = Guid.NewGuid().ToByteArray();
        byte[] journalSeed = Guid.NewGuid().ToByteArray();
        try
        {
            var invocation = new InstallerMachineHelperInvocation(
                InstallerMachineHelperVerb.Prepare,
                Convert.ToHexStringLower(SHA256.HashData(transactionSeed)),
                Convert.ToHexStringLower(SHA256.HashData(journalSeed)));
            return InstallerMachineHelperBootstrap.Create(
                invocation,
                Environment.ProcessId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transactionSeed);
            CryptographicOperations.ZeroMemory(journalSeed);
        }
    }

    private static void AssertExactRule(
        IReadOnlyList<PipeAccessRule> rules,
        SecurityIdentifier sid,
        PipeAccessRights rights,
        AccessControlType type)
    {
        PipeAccessRule rule = Assert.Single(rules, candidate =>
            Equals(candidate.IdentityReference, sid)
            && candidate.AccessControlType == type);
        Assert.Equal(rights, rule.PipeAccessRights);
        Assert.False(rule.IsInherited);
    }

    private sealed class FakePipeIdentityNative : IWindowsMachineHelperPipeIdentityNative
    {
        internal uint ClientProcessId { get; init; }

        internal uint ServerProcessId { get; init; }

        internal Exception? Failure { get; init; }

        internal int ClientQueries { get; private set; }

        internal int ServerQueries { get; private set; }

        public uint GetClientProcessId(SafePipeHandle connectedServerPipe)
        {
            ClientQueries++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return ClientProcessId;
        }

        public uint GetServerProcessId(SafePipeHandle connectedClientPipe)
        {
            ServerQueries++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return ServerProcessId;
        }
    }
}
