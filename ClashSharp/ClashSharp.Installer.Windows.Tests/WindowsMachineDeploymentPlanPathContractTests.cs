using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Windows.Machines;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Path-derivation contracts of the machine deployment plan over real account names, relocated
/// Program Files or ProgramData roots, case differences and command lengths: every derived path is an
/// exact descendant of its root, the SCM command line round-trips through the real Windows parser, and
/// anything Windows would reinterpret is rejected with the root's own stable code.
/// </summary>
public sealed class WindowsMachineDeploymentPlanPathContractTests
{
    private const string TargetSid = "S-1-5-21-100-200-300-1001";
    private const string Token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void EveryCorpusProfileYieldsExactDescendantPathsAndARoundTrippingServiceCommand()
    {
        using var fixture = Fixture();
        foreach (string parent in WindowsPathCorpus.ProfileParents)
        {
            foreach (string leaf in WindowsPathCorpus.ProfileLeaves)
            {
                string profile = parent + @"\" + leaf;

                WindowsMachineDeploymentPlan plan = Create(fixture, profile: profile);

                Assert.Equal(profile, plan.TargetProfileRoot);
                WindowsMachineDeploymentPlan.RequireExactDescendant(profile, plan.ConfigPath, "test.not_descendant");
                Assert.StartsWith(profile + @"\AppData\Local\Packages\", plan.ConfigPath, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(@"\LocalState\mihomo\config.yaml", plan.ConfigPath, StringComparison.Ordinal);
                string[] argv = ParseCommandLine(plan.Service.BinaryPath);
                Assert.Equal(11, argv.Length);
                Assert.Equal(plan.ServiceHostPath, argv[0]);
                Assert.Equal("--mihomo", argv[1]);
                Assert.Equal(plan.MihomoPath, argv[2]);
                Assert.Equal("--config", argv[3]);
                Assert.Equal(plan.ConfigPath, argv[4]);
                Assert.Equal("--pipe-name", argv[5]);
                Assert.Equal("--ipc-token", argv[7]);
                Assert.Equal(Token, argv[8]);
                Assert.Equal("--allowed-sid", argv[9]);
                Assert.Equal(TargetSid, argv[10]);
                plan.Validate();
            }
        }
    }

    [Fact]
    public void ProfileSpellingNeverChangesAnythingExceptTheConfigPathAndTheServiceCommand()
    {
        using var fixture = Fixture();
        WindowsMachineDeploymentPlan baseline = Create(fixture, profile: @"C:\Users\owner");

        foreach (string leaf in WindowsPathCorpus.ProfileLeaves)
        {
            WindowsMachineDeploymentPlan plan = Create(fixture, profile: @"C:\Users\" + leaf);

            Assert.Equal(baseline.MachineRoot, plan.MachineRoot);
            Assert.Equal(baseline.CurrentRoot, plan.CurrentRoot);
            Assert.Equal(baseline.StagingRoot, plan.StagingRoot);
            Assert.Equal(baseline.PreviousRoot, plan.PreviousRoot);
            Assert.Equal(baseline.ServiceDataRoot, plan.ServiceDataRoot);
            Assert.Equal(baseline.AssociationPath, plan.AssociationPath);
            Assert.Equal(
                baseline.PayloadTargets.Select(static target => target.DestinationPath),
                plan.PayloadTargets.Select(static target => target.DestinationPath));
            Assert.Equal(baseline.Service.ServiceName, plan.Service.ServiceName);
            Assert.Equal(baseline.Service.AccountName, plan.Service.AccountName);
        }
    }

    [Fact]
    public void ProgramFilesAndProgramDataRootsMayBeRelocatedAndAreDerivedInTheirOwnCase()
    {
        using var fixture = Fixture();
        foreach (string programFiles in new[] { @"C:\Program Files", @"D:\Program Files", @"c:\program files", @"E:\Apps (64-bit)\Programme", @"C:\程序文件" })
        {
            foreach (string programData in new[] { @"C:\ProgramData", @"D:\Shared Data", @"c:\programdata", @"C:\数据" })
            {
                foreach (string trailing in new[] { string.Empty, @"\" })
                {
                    WindowsMachineDeploymentPlan plan = Create(
                        fixture,
                        programFiles: programFiles + trailing,
                        programData: programData + trailing);

                    Assert.Equal(programFiles + @"\ClashSharp\Service", plan.MachineRoot);
                    Assert.Equal(programData + @"\ClashSharp\MihomoService", plan.ServiceDataRoot);
                    Assert.Equal(programFiles, plan.ProgramFilesRoot);
                    Assert.Equal(programData, plan.CommonApplicationDataRoot);
                    Assert.Equal(
                        plan.MachineRoot,
                        WindowsMachineDeploymentRoots.Create(programFiles, programData).MachineRoot);
                }
            }
        }
    }

    [Fact]
    public void ProfileThatAliasesAMachineRootInAnyCaseOrSeparatorFormIsRejected()
    {
        using var fixture = Fixture();
        foreach (string alias in new[]
        {
            @"C:\Program Files", @"c:\PROGRAM FILES", @"C:\Program Files\", @"C:\ProgramData", @"c:\programdata\",
        })
        {
            InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
                Create(fixture, profile: alias));

            Assert.Equal("installer.machine.root_identity_invalid", failure.DiagnosticCode);
        }

        foreach (string alias in new[] { @"C:\PROGRAMDATA", @"c:\programdata\" })
        {
            Assert.Equal(
                "installer.machine.root_identity_invalid",
                Assert.Throws<InstallerProtocolException>(() => Create(
                    fixture,
                    programFiles: @"C:\ProgramData",
                    programData: alias)).DiagnosticCode);
        }
    }

    [Fact]
    public void EveryNonCanonicalRootSpellingIsRejectedWithTheRootsOwnStableCode()
    {
        using var fixture = Fixture();
        foreach (string raw in WindowsPathCorpus.NonCanonicalProfilePaths)
        {
            string path = raw;
            Assert.Equal(
                "installer.machine.target_profile_invalid",
                Assert.Throws<InstallerProtocolException>(() => Create(fixture, profile: path)).DiagnosticCode);
            Assert.Equal(
                "installer.machine.program_files_invalid",
                Assert.Throws<InstallerProtocolException>(() => Create(fixture, programFiles: path)).DiagnosticCode);
            Assert.Equal(
                "installer.machine.common_application_data_invalid",
                Assert.Throws<InstallerProtocolException>(() => Create(fixture, programData: path)).DiagnosticCode);
        }
    }

    [Fact]
    public void ServiceCommandLengthIsBoundedExactlyAtTheScmLimit()
    {
        using var fixture = Fixture();
        const string shortProfile = @"C:\Users\x";
        int baseline = Create(fixture, profile: shortProfile).Service.BinaryPath.Length;
        int budget = WindowsMachineDeploymentPlan.MaximumServiceCommandCharacters - baseline;

        WindowsMachineDeploymentPlan atLimit = Create(fixture, profile: shortProfile + new string('p', budget));
        Assert.Equal(WindowsMachineDeploymentPlan.MaximumServiceCommandCharacters, atLimit.Service.BinaryPath.Length);

        InstallerProtocolException tooLong = Assert.Throws<InstallerProtocolException>(() =>
            Create(fixture, profile: shortProfile + new string('p', budget + 1)));
        Assert.Equal("installer.machine.service_command_too_long", tooLong.DiagnosticCode);
    }

    [Fact]
    public void ProfileNamesThatWouldBreakTheQuotedServiceCommandAreRejectedNotEscaped()
    {
        using var fixture = Fixture();
        foreach (string unsafeLeaf in new[] { "a\"b", "a\tb", "a\nb", "a\rb", "a\0b", "a\u001Fb" })
        {
            Exception? failure = Record.Exception(() => Create(fixture, profile: @"C:\Users\" + unsafeLeaf));

            InstallerProtocolException protocol = Assert.IsType<InstallerProtocolException>(failure);
            Assert.Equal("installer.machine.target_profile_invalid", protocol.DiagnosticCode);
        }
    }

    [Fact]
    public void PayloadDestinationsStayInsideTheCurrentRootRegardlessOfProgramFilesSpelling()
    {
        using var fixture = Fixture();
        foreach (string programFiles in new[] { @"C:\Program Files", @"c:\PROGRAM FILES\", @"D:\Árvíztűrő Tükörfúrógép" })
        {
            WindowsMachineDeploymentPlan plan = Create(fixture, programFiles: programFiles);

            Assert.All(plan.PayloadTargets, target =>
            {
                WindowsMachineDeploymentPlan.RequireExactDescendant(
                    plan.CurrentRoot,
                    target.DestinationPath,
                    "test.escaped_current_root");
                Assert.Equal(
                    target.DestinationPath,
                    Path.GetFullPath(target.DestinationPath));
            });
            Assert.Equal(
                plan.PayloadTargets.Count,
                plan.PayloadTargets
                    .Select(static target => target.DestinationPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        }
    }

    [Fact]
    public void DescendantRelationIsStrictCaseInsensitiveAndNeverMatchesASiblingPrefix()
    {
        const string code = "test.not_descendant";
        WindowsMachineDeploymentPlan.RequireExactDescendant(@"C:\Root", @"C:\Root\child", code);
        WindowsMachineDeploymentPlan.RequireExactDescendant(@"C:\Root\", @"c:\ROOT\Child\leaf", code);
        foreach (string candidate in new[]
        {
            @"C:\Root", @"C:\Root\", @"C:\Rootx", @"C:\Root2\child", @"C:\Roo", @"C:\Other\Root\child",
            @"C:\Root\..\Rootx", @"D:\Root\child", @"C:\Root\child\..",
        })
        {
            Exception? failure = Record.Exception(() =>
                WindowsMachineDeploymentPlan.RequireExactDescendant(@"C:\Root", candidate, code));

            Assert.True(
                failure is InstallerProtocolException { DiagnosticCode: code },
                $"'{candidate}' was treated as a strict descendant of the root.");
        }
    }

    [Fact]
    public void DerivedPathsCannotEscapeTheirRootThroughTraversalSegments()
    {
        foreach (string[] segments in new[]
        {
            new[] { ".." }, new[] { "a", "..", ".." }, new[] { @"..\x" }, new[] { @"a\..\..\x" },
            new[] { "a", @"..\..\b" }, new[] { @"C:\Other" }, new[] { @"\Other" },
        })
        {
            Assert.Equal(
                "installer.machine.derived_path_invalid",
                Assert.Throws<InstallerProtocolException>(() =>
                    WindowsMachineDeploymentPlan.Descendant(@"C:\Root", segments)).DiagnosticCode);
        }

        Assert.Equal(
            @"C:\Root\a\b",
            WindowsMachineDeploymentPlan.Descendant(@"C:\Root", "a", "b"));
    }

    private static WindowsPayloadFixture Fixture() => new(
        createPayload: false,
        removeCurrentUserCertificateOnDispose: false);

    private static WindowsMachineDeploymentPlan Create(
        WindowsPayloadFixture fixture,
        string programFiles = @"C:\Program Files",
        string programData = @"C:\ProgramData",
        string profile = @"C:\Users\owner") =>
        WindowsMachineDeploymentPlan.Create(
            fixture.Request(targetSid: TargetSid),
            fixture.Manifest,
            InstallerMachineAssociation.Create(TargetSid, Token),
            programFiles,
            programData,
            profile);

    /// <summary>Parses with the same routine Windows applies to a service image path.</summary>
    private static string[] ParseCommandLine(string commandLine)
    {
        nint argv = NativeMethods.CommandLineToArgvW(commandLine, out int count);
        Assert.NotEqual(nint.Zero, argv);
        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * nint.Size))!;
            }

            return result;
        }
        finally
        {
            _ = NativeMethods.LocalFree(argv);
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint LocalFree(nint memory);
    }
}
