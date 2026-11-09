using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Platform;
using ClashSharp.Installer.Windows.Packages;
using ClashSharp.Installer.Windows.Platform;
using Windows.Management.Deployment;

namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Contracts of the thin Windows seams: native-architecture and product-type mapping (including the
/// 32-bit versus 64-bit process view), the package-deployment option mapping, and the read-only process
/// catalog. Every enum or flag in the mapped universe is enumerated so a new member is covered.
/// </summary>
public sealed class WindowsPlatformAndPackageSeamContractTests
{
    [Fact]
    public void ProductTypeMapsToWorkstationOnlyForTheWorkstationValueAcrossTheWholeByteRange()
    {
        for (int type = 0; type <= byte.MaxValue; type++)
        {
            InstallerPlatformFacts facts = WindowsInstallerPlatformProbe.CreateFacts(
                (byte)type,
                26100,
                9,
                Architecture.X64,
                "Server");

            Assert.True(facts.IsWindows);
            Assert.Equal(type == 1, facts.IsWorkstation);
            Assert.Equal(type is 2 or 3, facts.IsServerDesktopExperience);
            Assert.Equal(26100, facts.BuildNumber);
        }
    }

    [Fact]
    public void ServerDesktopExperienceRequiresTheExactOrdinalInstallationTypeOnAServerProductOnly()
    {
        foreach (string? installationType in new[]
        {
            null, string.Empty, "server", "SERVER", "Server ", " Server", "Server Core", "Nano Server", "Client", "Server\0", "Ｓerver",
        })
        {
            foreach (byte type in new byte[] { 2, 3 })
            {
                Assert.False(
                    WindowsInstallerPlatformProbe.CreateFacts(type, 26100, 9, Architecture.X64, installationType).IsServerDesktopExperience,
                    $"'{installationType}' must not be treated as the desktop-experience Server installation.");
            }
        }

        Assert.True(WindowsInstallerPlatformProbe.CreateFacts(3, 26100, 9, Architecture.X64, "Server").IsServerDesktopExperience);
        Assert.False(WindowsInstallerPlatformProbe.CreateFacts(1, 26100, 9, Architecture.X64, "Server").IsServerDesktopExperience);
    }

    [Fact]
    public void EveryNativeProcessorArchitectureValueMapsToExactlyItsKnownFamilyOrUnknown()
    {
        var known = new Dictionary<ushort, InstallerCpuArchitecture>
        {
            [0] = InstallerCpuArchitecture.X86,
            [5] = InstallerCpuArchitecture.Arm,
            [9] = InstallerCpuArchitecture.X64,
            [12] = InstallerCpuArchitecture.Arm64,
        };
        for (int value = 0; value <= ushort.MaxValue; value++)
        {
            InstallerCpuArchitecture expected = known.TryGetValue((ushort)value, out InstallerCpuArchitecture mapped)
                ? mapped
                : InstallerCpuArchitecture.Unknown;

            Assert.Equal(
                expected,
                WindowsInstallerPlatformProbe.CreateFacts(1, 26100, (ushort)value, Architecture.X64, null).OperatingSystemArchitecture);
        }
    }

    [Fact]
    public void EveryRuntimeArchitectureMapsIndependentlyOfTheNativeOneSoAnEmulatedProcessIsVisible()
    {
        var known = new Dictionary<Architecture, InstallerCpuArchitecture>
        {
            [Architecture.X86] = InstallerCpuArchitecture.X86,
            [Architecture.X64] = InstallerCpuArchitecture.X64,
            [Architecture.Arm] = InstallerCpuArchitecture.Arm,
            [Architecture.Arm64] = InstallerCpuArchitecture.Arm64,
        };
        foreach (Architecture process in Enum.GetValues<Architecture>())
        {
            foreach (ushort native in new ushort[] { 0, 5, 9, 12, 14 })
            {
                InstallerPlatformFacts facts = WindowsInstallerPlatformProbe.CreateFacts(1, 26100, native, process, null);

                Assert.Equal(
                    known.TryGetValue(process, out InstallerCpuArchitecture mapped) ? mapped : InstallerCpuArchitecture.Unknown,
                    facts.ProcessArchitecture);
            }
        }

        // x64 emulation on an Arm64 machine: both facts must survive so policy can reject or accept it.
        InstallerPlatformFacts emulated = WindowsInstallerPlatformProbe.CreateFacts(1, 26100, 12, Architecture.X64, null);
        Assert.Equal(InstallerCpuArchitecture.Arm64, emulated.OperatingSystemArchitecture);
        Assert.Equal(InstallerCpuArchitecture.X64, emulated.ProcessArchitecture);
    }

    [Fact]
    public void LiveProbeAgreesWithTheRuntimeOnVersionAndBothArchitectureViews()
    {
        InstallerPlatformFacts facts = new WindowsInstallerPlatformProbe().Inspect(CancellationToken.None);

        Assert.True(facts.IsWindows);
        Assert.Equal(Environment.OSVersion.Version.Build, facts.BuildNumber);
        Assert.Equal(
            RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => InstallerCpuArchitecture.X64,
                Architecture.Arm64 => InstallerCpuArchitecture.Arm64,
                Architecture.X86 => InstallerCpuArchitecture.X86,
                Architecture.Arm => InstallerCpuArchitecture.Arm,
                _ => InstallerCpuArchitecture.Unknown,
            },
            facts.OperatingSystemArchitecture);
        Assert.Equal(
            RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => InstallerCpuArchitecture.X64,
                Architecture.Arm64 => InstallerCpuArchitecture.Arm64,
                Architecture.X86 => InstallerCpuArchitecture.X86,
                Architecture.Arm => InstallerCpuArchitecture.Arm,
                _ => InstallerCpuArchitecture.Unknown,
            },
            facts.ProcessArchitecture);
    }

    [Fact]
    public void LiveProbeHonoursCancellationBeforeAnyNativeCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new WindowsInstallerPlatformProbe().Inspect(cancellation.Token));
    }

    [Fact]
    public void EveryDeploymentFlagReachesOnlyItsOwnPackageOptionAndNoOtherOptionDefaultsOn()
    {
        PropertyInfo[] flags = typeof(WindowsPackageDeploymentRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.PropertyType == typeof(bool))
            .ToArray();
        Assert.True(flags.Length >= 10);

        foreach (PropertyInfo flag in flags)
        {
            AddPackageOptions options = WindowsPackageManagerFacade.CreateOptions(Request(flag.Name));

            PropertyInfo target = typeof(AddPackageOptions).GetProperty(flag.Name)
                ?? throw new InvalidOperationException($"{flag.Name} has no package option counterpart.");
            Assert.True((bool)target.GetValue(options)!, $"{flag.Name} did not reach its option.");
            foreach (PropertyInfo other in typeof(AddPackageOptions).GetProperties().Where(static property => property.PropertyType == typeof(bool)))
            {
                if (other.Name != flag.Name)
                {
                    Assert.False((bool)other.GetValue(options)!, $"Setting {flag.Name} also enabled {other.Name}.");
                }
            }
        }

        AddPackageOptions defaults = WindowsPackageManagerFacade.CreateOptions(Request(null));
        Assert.All(
            typeof(AddPackageOptions).GetProperties().Where(static property => property.PropertyType == typeof(bool)),
            property => Assert.False((bool)property.GetValue(defaults)!, property.Name));
    }

    [Fact]
    public void DependencyUrisKeepTheirOrderAndNothingElseIsAddedToTheOptions()
    {
        Uri[] dependencies =
        [
            new(@"C:\Payload\dep one.msix"),
            new(@"C:\用户\依赖.msix"),
            new(@"C:\Payload\dep three.msix"),
        ];
        var request = Request(null) with { DependencyPackageUris = dependencies };

        AddPackageOptions options = WindowsPackageManagerFacade.CreateOptions(request);

        Assert.Equal(dependencies, options.DependencyPackageUris.ToArray());
        Assert.Empty(WindowsPackageManagerFacade.CreateOptions(Request(null)).DependencyPackageUris);
        Assert.Throws<ArgumentNullException>(() => WindowsPackageManagerFacade.CreateOptions(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("a*")]
    [InlineData("a?")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\0b")]
    [InlineData("a\"b")]
    public void ProcessCatalogRejectsEveryNameThatIsNotAPlainExecutableBaseName(string name)
    {
        InstallerProtocolException failure = Assert.Throws<InstallerProtocolException>(() =>
            WindowsPackageProcessCatalog.Instance.ObserveCandidates(name, CancellationToken.None));

        Assert.Equal("installer.application_executable_invalid", failure.DiagnosticCode);
    }

    [Fact]
    public void ProcessCatalogFindsNothingForAMissingExecutableAndClassifiesTheUnpackagedTestHost()
    {
        IReadOnlyList<WindowsPackageProcessObservation> none = WindowsPackageProcessCatalog.Instance.ObserveCandidates(
            "ClashSharpNoSuchProcess" + Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        Assert.Empty(none);

        using Process self = Process.GetCurrentProcess();
        IReadOnlyList<WindowsPackageProcessObservation> own = WindowsPackageProcessCatalog.Instance.ObserveCandidates(
            self.ProcessName,
            CancellationToken.None);

        Assert.NotEmpty(own);
        Assert.All(own, observation =>
        {
            Assert.True(Enum.IsDefined(observation.Kind));
            Assert.Equal(
                observation.Kind == WindowsPackageProcessObservationKind.Packaged,
                !string.IsNullOrWhiteSpace(observation.PackageFamilyName));
        });
        Assert.Contains(own, static observation => observation.Kind == WindowsPackageProcessObservationKind.Unpackaged);
    }

    [Fact]
    public void ProcessCatalogHonoursCancellationBeforeEnumeratingProcesses()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            WindowsPackageProcessCatalog.Instance.ObserveCandidates("ClashSharp", cancellation.Token));
    }

    private static WindowsPackageDeploymentRequest Request(string? enabledFlag)
    {
        var request = new WindowsPackageDeploymentRequest(
            new Uri(@"C:\Payload\app.msix"),
            [],
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false);
        if (enabledFlag is null)
        {
            return request;
        }

        PropertyInfo property = typeof(WindowsPackageDeploymentRequest).GetProperty(
            enabledFlag,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        ConstructorInfo constructor = typeof(WindowsPackageDeploymentRequest).GetConstructors().Single();
        object?[] arguments = constructor.GetParameters()
            .Select(parameter => string.Equals(parameter.Name, enabledFlag, StringComparison.Ordinal)
                ? true
                : typeof(WindowsPackageDeploymentRequest).GetProperty(
                    parameter.Name!,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(request))
            .ToArray();
        Assert.NotNull(property);
        return (WindowsPackageDeploymentRequest)constructor.Invoke(arguments);
    }
}
