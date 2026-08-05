using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Verifies service startup identity, secret handling, and named-pipe ACL boundaries.</summary>
public sealed class MihomoServiceOptionsAndSecurityTests
{
    /// <summary>Verifies every endpoint argument is required and aligned to the shared identity.</summary>
    [Fact]
    public void Parse_ValidatesExactEndpointArguments()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string pipeName = MihomoServiceIpcProtocol.BuildPipeName(
            MihomoServiceTestSupport.TestUserSid.Value,
            MihomoServiceTestSupport.Token);
        string[] validArguments =
        [
            "--mihomo", Path.Combine(temporaryDirectory.Path, "mihomo.exe"),
            "--config", Path.Combine(temporaryDirectory.Path, "runtime.yaml"),
            "--pipe-name", pipeName,
            "--ipc-token", MihomoServiceTestSupport.Token,
            "--allowed-sid", MihomoServiceTestSupport.TestUserSid.Value,
        ];

        MihomoServiceOptions options = MihomoServiceOptions.Parse(validArguments);

        Assert.Equal(pipeName, options.PipeName);
        Assert.Equal(MihomoServiceTestSupport.Token, options.IpcToken);
        Assert.Equal(MihomoServiceTestSupport.TestUserSid, options.AllowedSid);
        Assert.DoesNotContain(MihomoServiceTestSupport.Token, options.ToString(), StringComparison.Ordinal);

        string[] missing = validArguments[..^2];
        string[] duplicate = [.. validArguments, "--ipc-token", MihomoServiceTestSupport.Token];
        string[] unknown = (string[])validArguments.Clone();
        unknown[0] = "--unexpected";
        string[] mismatchedPipe = (string[])validArguments.Clone();
        mismatchedPipe[7] = new string('0', 64);
        string[] malformedToken = (string[])validArguments.Clone();
        malformedToken[7] = MihomoServiceTestSupport.Token.ToUpperInvariant();
        string[] userWorkDirectory =
        [
            .. validArguments,
            "--workdir", Path.Combine(temporaryDirectory.Path, "work"),
        ];

        Assert.Throws<ArgumentException>(() => MihomoServiceOptions.Parse(missing));
        Assert.Throws<ArgumentException>(() => MihomoServiceOptions.Parse(duplicate));
        Assert.Throws<ArgumentException>(() => MihomoServiceOptions.Parse(unknown));
        Assert.Throws<ArgumentException>(() => MihomoServiceOptions.Parse(userWorkDirectory));
        Assert.Throws<ArgumentException>(() => MihomoServiceOptions.Parse(mismatchedPipe));
        ArgumentException tokenFailure = Assert.Throws<ArgumentException>(
            () => MihomoServiceOptions.Parse(malformedToken));
        Assert.DoesNotContain(
            MihomoServiceTestSupport.Token.ToUpperInvariant(),
            tokenFailure.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>Verifies broad system principals cannot be substituted for the target user SID.</summary>
    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.NetworkSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public void Constructor_RejectsBroadOrPrivilegedAllowedSid(WellKnownSidType sidType)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        SecurityIdentifier sid = new(sidType, null);
        string pipeName = MihomoServiceIpcProtocol.BuildPipeName(
            sid.Value,
            MihomoServiceTestSupport.Token);

        Assert.Throws<ArgumentException>(() => new MihomoServiceOptions(
            Path.Combine(temporaryDirectory.Path, "mihomo.exe"),
            Path.Combine(temporaryDirectory.Path, "runtime.yaml"),
            pipeName,
            MihomoServiceTestSupport.Token,
            sid,
            Path.Combine(temporaryDirectory.Path, "staged")));
    }

    /// <summary>Verifies the protected DACL grants only the target user and local administrators.</summary>
    [Fact]
    public void PipeSecurity_UsesProtectedExplicitRules()
    {
        PipeSecurity security = MihomoServicePipeSecurity.Create(
            MihomoServiceTestSupport.TestUserSid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule[] pipeRules = rules.Cast<PipeAccessRule>().ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Contains(pipeRules, rule =>
            IsRule(rule, WellKnownSidType.NetworkSid, AccessControlType.Deny)
            && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        PipeAccessRule userRule = Assert.Single(pipeRules, rule =>
            Equals(rule.IdentityReference, MihomoServiceTestSupport.TestUserSid)
            && rule.AccessControlType == AccessControlType.Allow);
        Assert.True(userRule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(userRule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.Contains(pipeRules, rule =>
            IsRule(rule, WellKnownSidType.LocalSystemSid, AccessControlType.Allow)
            && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.Contains(pipeRules, rule =>
            IsRule(rule, WellKnownSidType.BuiltinAdministratorsSid, AccessControlType.Allow)
            && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    /// <summary>Verifies service data directories are LocalSystem-owned with no inherited user ACL.</summary>
    [Fact]
    public void GenerationDirectorySecurity_IsProtectedForSystemAndAdministrators()
    {
        DirectorySecurity security = MihomoGenerationStore.CreateProtectedDirectorySecurity();
        SecurityIdentifier owner = Assert.IsType<SecurityIdentifier>(
            security.GetOwner(typeof(SecurityIdentifier)));
        FileSystemAccessRule[] rules = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.True(owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.True(security.AreAccessRulesProtected);
        Assert.Contains(rules, rule =>
            rule.IdentityReference is SecurityIdentifier sid
            && sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, rule =>
            rule.IdentityReference is SecurityIdentifier sid
            && sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    /// <summary>Verifies staged files themselves cannot retain a pre-created user ACL.</summary>
    [Fact]
    public void GenerationFileSecurity_IsProtectedForSystemAndAdministrators()
    {
        FileSecurity security = MihomoGenerationStore.CreateProtectedFileSecurity();
        SecurityIdentifier owner = Assert.IsType<SecurityIdentifier>(
            security.GetOwner(typeof(SecurityIdentifier)));
        FileSystemAccessRule[] rules = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        Assert.True(owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            SecurityIdentifier sid = Assert.IsType<SecurityIdentifier>(rule.IdentityReference);
            Assert.True(sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        });
    }

    /// <summary>Verifies every owned path component rejects reparse-point attributes.</summary>
    [Fact]
    public void GenerationPathValidation_RejectsReparsePoint()
    {
        Assert.Throws<IOException>(() => MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            "product data root"));
    }

    /// <summary>Verifies a pre-positioned product-root link is rejected before endpoint creation.</summary>
    [Fact]
    public async Task GenerationStore_RejectsPreexistingParentReparsePoint()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string targetPath = Path.Combine(temporaryDirectory.Path, "link-target");
        Directory.CreateDirectory(targetPath);
        string productPath = Path.Combine(temporaryDirectory.Path, "ClashSharp");
        _ = Directory.CreateSymbolicLink(productPath, targetPath);

        string pipeName = MihomoServiceIpcProtocol.BuildPipeName(
            MihomoServiceTestSupport.TestUserSid.Value,
            MihomoServiceTestSupport.Token);
        MihomoServiceOptions options = new(
            Path.Combine(temporaryDirectory.Path, "mihomo.exe"),
            Path.Combine(temporaryDirectory.Path, "runtime.yaml"),
            pipeName,
            MihomoServiceTestSupport.Token,
            MihomoServiceTestSupport.TestUserSid,
            Path.Combine(productPath, "MihomoService", pipeName));
        string content = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n");
        File.WriteAllText(options.ConfigPath, content);
        MihomoGenerationStore store = new(
            options,
            protectDirectory: true,
            commonApplicationDataRoot: temporaryDirectory.Path);

        await Assert.ThrowsAsync<IOException>(() => store.StageAsync(
            1,
            MihomoServiceTestSupport.ComputeHash(content),
            CancellationToken.None));
    }

    /// <summary>Verifies staged history is bounded while a protected active generation survives.</summary>
    [Fact]
    public async Task GenerationStore_RetainsEightFilesWithoutDeletingActiveStage()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoGenerationStore store = new(options, protectDirectory: false);
        string? activePath = null;
        string? latestPath = null;
        for (int generation = 1; generation <= 10; generation++)
        {
            string content = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
                $"mixed-port: {7800 + generation}\n");
            File.WriteAllText(options.ConfigPath, content);
            MihomoStagedGeneration staged = await store.StageAsync(
                generation,
                MihomoServiceTestSupport.ComputeHash(content),
                CancellationToken.None,
                activePath);
            activePath ??= staged.ConfigurationPath;
            latestPath = staged.ConfigurationPath;
        }

        string[] retained = Directory.GetFiles(options.ServiceDataDirectory, "generation-*.yaml");

        Assert.Equal(8, retained.Length);
        Assert.Contains(activePath, retained, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(latestPath, retained, StringComparer.OrdinalIgnoreCase);
        Assert.All(retained, path =>
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)));
    }

    /// <summary>Verifies FirstPipeInstance prevents a pre-existing endpoint from being reused.</summary>
    [Fact]
    public void CreateServerStream_RejectsSecondPipeInstance()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            .PadRight(64, '0')
            .ToLowerInvariant();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(
            temporaryDirectory.Path,
            token: token);
        using NamedPipeServerStream first = MihomoServicePipeServer.CreateServerStream(options);

        Exception? exception = Record.Exception(
            () => MihomoServicePipeServer.CreateServerStream(options));
        Assert.True(exception is IOException or UnauthorizedAccessException);
    }

    /// <summary>Verifies the bounded log ring redacts the authentication token from child output.</summary>
    [Fact]
    public void LogBuffer_IsBoundedAndRedactsToken()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoServiceLogBuffer logs = new(options);
        for (int index = 0; index < 1100; index++)
        {
            logs.Append("stdout", $"entry-{index} {MihomoServiceTestSupport.Token}");
        }

        IReadOnlyList<string> latest = logs.ReadLatest(MihomoServiceIpcProtocol.MaximumLogEntries);

        Assert.Equal(MihomoServiceIpcProtocol.MaximumLogEntries, latest.Count);
        Assert.Contains("entry-844", latest[0], StringComparison.Ordinal);
        Assert.All(latest, entry =>
        {
            Assert.DoesNotContain(MihomoServiceTestSupport.Token, entry, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", entry, StringComparison.Ordinal);
        });
    }

    /// <summary>Verifies dynamic controller capabilities are redacted before retention.</summary>
    [Fact]
    public void LogBuffer_RedactsRegisteredControllerAuthority()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoServiceLogBuffer logs = new(options);
        const string secret =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string pipe =
            @"\\.\pipe\ClashSharp.Mihomo.Controller.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        logs.RegisterSensitiveValue(secret);
        logs.RegisterSensitiveValue(pipe);

        logs.Append("stdout", $"controller={pipe} secret={secret}");

        string entry = Assert.Single(logs.ReadLatest(1));
        Assert.DoesNotContain(secret, entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pipe, entry, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, entry.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }

    /// <summary>Verifies final service-log truncation accounts for its timestamp/category prefix.</summary>
    [Fact]
    public void LogBuffer_PreservesSurrogateBoundaryAfterEntryPrefix()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoServiceLogBuffer logs = new(options);
        logs.Append("stdout", "prefix-probe");
        string probe = Assert.Single(logs.ReadLatest(1));
        int prefixLength = probe.Length - "prefix-probe".Length;
        string message = new string(
            'a',
            MihomoServiceIpcProtocol.MaximumLogEntryCharacters - prefixLength - 1) + "😀";

        logs.Append("stdout", message);

        string entry = logs.ReadLatest(1)[0];
        Assert.Equal(MihomoServiceIpcProtocol.MaximumLogEntryCharacters - 1, entry.Length);
        Assert.DoesNotContain(entry, char.IsSurrogate);
    }

    /// <summary>Verifies runtime-log truncation never emits malformed UTF-16.</summary>
    [Fact]
    public void RuntimeLogBuffer_PreservesSurrogateBoundariesAtWireLimit()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoServiceLogBuffer serviceLogs = new(options);
        MihomoRuntimeLogBuffer runtimeLogs = new(serviceLogs);
        string message = new string(
            'a',
            MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters - 1) + "😀";

        runtimeLogs.Append("stdout", message);

        string projected = Assert.Single(runtimeLogs.ReadAfter(0, 1).Entries).Message;
        Assert.Equal(
            MihomoServiceIpcProtocol.MaximumRuntimeLogMessageCharacters - 1,
            projected.Length);
        Assert.DoesNotContain(projected, char.IsSurrogate);
    }

    /// <summary>Verifies one runtime-log poll always remains below the IPC aggregate limit.</summary>
    [Fact]
    public void RuntimeLogBuffer_BoundsAggregatePayload()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        MihomoRuntimeLogBuffer runtimeLogs = new(new MihomoServiceLogBuffer(options));
        for (int index = 0; index < MihomoServiceIpcProtocol.MaximumRuntimeLogEntries; index++)
        {
            runtimeLogs.Append("stdout", new string('x', 4096));
        }

        MihomoServiceIpcRuntimeLogSnapshot snapshot = runtimeLogs.ReadAfter(
            0,
            MihomoServiceIpcProtocol.MaximumRuntimeLogEntries);

        Assert.True(snapshot.Entries.Count < MihomoServiceIpcProtocol.MaximumRuntimeLogEntries);
        Assert.True(snapshot.Entries.Sum(entry => entry.Message.Length)
            <= MihomoServiceIpcProtocol.MaximumControllerAggregateCharacters);
        Assert.Null(snapshot.Validate());
    }

    private static bool IsRule(
        PipeAccessRule rule,
        WellKnownSidType sidType,
        AccessControlType accessControlType)
    {
        return rule.IdentityReference is SecurityIdentifier sid
            && sid.IsWellKnown(sidType)
            && rule.AccessControlType == accessControlType;
    }
}
