using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsServiceProcessType : uint
{
    OwnProcess = 0x0000_0010,
    SharedProcess = 0x0000_0020,
}

internal enum WindowsServiceStartMode : uint
{
    Boot = 0x0000_0000,
    System = 0x0000_0001,
    Automatic = 0x0000_0002,
    Demand = 0x0000_0003,
    Disabled = 0x0000_0004,
}

internal enum WindowsServiceErrorMode : uint
{
    Ignore = 0x0000_0000,
    Normal = 0x0000_0001,
    Severe = 0x0000_0002,
    Critical = 0x0000_0003,
}

internal sealed record WindowsServiceConfiguration(
    string ServiceName,
    string DisplayName,
    string Description,
    WindowsServiceProcessType ProcessType,
    WindowsServiceStartMode StartMode,
    WindowsServiceErrorMode ErrorMode,
    bool DelayedAutoStart,
    string AccountName,
    string BinaryPath,
    IReadOnlyList<string> Dependencies)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceName)
            || ServiceName.Length > 256
            || ServiceName.Contains('/', StringComparison.Ordinal)
            || ServiceName.Contains('\\', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(DisplayName)
            || Description is null
            || Description.Any(char.IsControl)
            || !Enum.IsDefined(ProcessType)
            || !Enum.IsDefined(StartMode)
            || !Enum.IsDefined(ErrorMode)
            || string.IsNullOrWhiteSpace(AccountName)
            || string.IsNullOrWhiteSpace(BinaryPath)
            || BinaryPath.Length > WindowsMachineDeploymentPlan.MaximumServiceCommandCharacters
            || BinaryPath.Any(char.IsControl)
            || Dependencies is null
            || Dependencies.Count > 64
            || Dependencies.Any(static dependency =>
                string.IsNullOrWhiteSpace(dependency)
                || dependency.Length > 256
                || dependency.Any(char.IsControl)))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_configuration_invalid");
        }
    }

    internal void ValidateExpected()
    {
        Validate();
        if (!string.Equals(
                ServiceName,
                WindowsMachineDeploymentPlan.ServiceName,
                StringComparison.Ordinal)
            || !string.Equals(
                DisplayName,
                WindowsMachineDeploymentPlan.ServiceDisplayName,
                StringComparison.Ordinal)
            || !string.Equals(
                Description,
                WindowsMachineDeploymentPlan.ServiceDescription,
                StringComparison.Ordinal)
            || ProcessType != WindowsServiceProcessType.OwnProcess
            || StartMode != WindowsServiceStartMode.Automatic
            || ErrorMode != WindowsServiceErrorMode.Normal
            || !DelayedAutoStart
            || !string.Equals(AccountName, "LocalSystem", StringComparison.Ordinal)
            || Dependencies.Count != 0)
        {
            throw new InstallerProtocolException(
                "installer.machine.service_configuration_invalid");
        }
    }
}

internal sealed record WindowsMachinePayloadTarget(
    InstallerMachinePayloadFileEntry Source,
    string RelativeTargetPath,
    string DestinationPath)
{
    internal void Validate(string currentRoot)
    {
        ArgumentNullException.ThrowIfNull(Source);
        Source.Validate();
        if (string.IsNullOrWhiteSpace(RelativeTargetPath)
            || Path.IsPathFullyQualified(RelativeTargetPath)
            || RelativeTargetPath.Contains('/', StringComparison.Ordinal)
            || RelativeTargetPath.Split(Path.DirectorySeparatorChar).Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_target_invalid");
        }

        WindowsMachineDeploymentPlan.RequireExactDescendant(
            currentRoot,
            DestinationPath,
            "installer.machine.payload_target_invalid");
        if (!string.Equals(
                Path.GetFullPath(Path.Combine(currentRoot, RelativeTargetPath)),
                DestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_target_invalid");
        }
    }
}

/// <summary>
/// Derives the complete machine layout and SCM tuple exclusively from signed release identity,
/// a validated association, trusted target-profile discovery, and Windows well-known folders.
/// </summary>
internal sealed class WindowsMachineDeploymentPlan
{
    internal const string ServiceName = "ClashSharpMihomo";
    internal const string ServiceDisplayName = "Clash# Mihomo Service";
    internal const string ServiceDescription = "Clash# local transparent-proxy host";
    internal const int MaximumServiceCommandCharacters = 32_767;

    private WindowsMachineDeploymentPlan(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        WindowsMachineDeploymentRoots roots,
        string targetProfileRoot)
    {
        Request = request;
        Manifest = manifest;
        Association = association;
        Roots = roots;
        ProgramFilesRoot = roots.ProgramFilesRoot;
        CommonApplicationDataRoot = roots.CommonApplicationDataRoot;
        TargetProfileRoot = targetProfileRoot;
        MachineRoot = roots.MachineRoot;
        CurrentRoot = Descendant(MachineRoot, "current");
        StagingRoot = Descendant(MachineRoot, "staging");
        PreviousRoot = Descendant(MachineRoot, "previous");
        ServiceHostPath = Descendant(
            CurrentRoot,
            "Host",
            "ClashSharp.MihomoService.exe");
        MihomoPath = Descendant(CurrentRoot, "mihomo.exe");
        GeoDataRoot = Descendant(CurrentRoot, "GeoData");
        ServiceDataRoot = roots.ServiceDataRoot;
        AssociationPath = Descendant(ServiceDataRoot, "association.json");
        ConfigPath = Descendant(
            targetProfileRoot,
            "AppData",
            "Local",
            "Packages",
            manifest.PackageIdentity.PackageFamilyName,
            "LocalState",
            "mihomo",
            "config.yaml");
        PayloadTargets = BuildPayloadTargets(manifest, CurrentRoot);
        Service = new WindowsServiceConfiguration(
            ServiceName,
            ServiceDisplayName,
            ServiceDescription,
            WindowsServiceProcessType.OwnProcess,
            WindowsServiceStartMode.Automatic,
            WindowsServiceErrorMode.Normal,
            DelayedAutoStart: true,
            AccountName: "LocalSystem",
            BuildServiceBinaryPath(),
            Dependencies: []);
        Validate();
    }

    internal InstallerRequest Request { get; }

    internal InstallerReleaseManifest Manifest { get; }

    internal InstallerMachineAssociation Association { get; }

    internal WindowsMachineDeploymentRoots Roots { get; }

    internal string ProgramFilesRoot { get; }

    internal string CommonApplicationDataRoot { get; }

    internal string TargetProfileRoot { get; }

    internal string MachineRoot { get; }

    internal string CurrentRoot { get; }

    internal string StagingRoot { get; }

    internal string PreviousRoot { get; }

    internal string ServiceHostPath { get; }

    internal string MihomoPath { get; }

    internal string GeoDataRoot { get; }

    internal string ServiceDataRoot { get; }

    internal string AssociationPath { get; }

    internal string ConfigPath { get; }

    internal IReadOnlyList<WindowsMachinePayloadTarget> PayloadTargets { get; }

    internal WindowsServiceConfiguration Service { get; }

    internal static WindowsMachineDeploymentPlan Create(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string programFilesRoot,
        string commonApplicationDataRoot,
        string targetProfileRoot) =>
        CreateCore(
            request,
            manifest,
            association,
            programFilesRoot,
            commonApplicationDataRoot,
            targetProfileRoot,
            removalPlan: false);

    internal static WindowsMachineDeploymentPlan CreateForRemoval(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string programFilesRoot,
        string commonApplicationDataRoot,
        string targetProfileRoot) =>
        CreateCore(
            request,
            manifest,
            association,
            programFilesRoot,
            commonApplicationDataRoot,
            targetProfileRoot,
            removalPlan: true);

    private static WindowsMachineDeploymentPlan CreateCore(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string programFilesRoot,
        string commonApplicationDataRoot,
        string targetProfileRoot,
        bool removalPlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(association);
        request.Validate();
        manifest.Validate();
        association.Validate();
        if (removalPlan != (request.Operation == InstallerOperation.Uninstall))
        {
            throw new InstallerProtocolException(
                "installer.machine.deployment_plan_operation_invalid");
        }

        if (!string.Equals(
                request.ExpectedPackageVersion,
                manifest.ExpectedPackageVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                request.InstallerPayloadSha256,
                manifest.InstallerPayloadSha256,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.release.identity_mismatch");
        }

        if (!string.Equals(
                request.TargetSid,
                association.OwnerSid,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.association_owner_mismatch");
        }

        WindowsMachineDeploymentRoots roots = WindowsMachineDeploymentRoots.Create(
            programFilesRoot,
            commonApplicationDataRoot);
        string profile = CanonicalRoot(
            targetProfileRoot,
            "installer.machine.target_profile_invalid");
        if (string.Equals(
                roots.ProgramFilesRoot,
                profile,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                roots.CommonApplicationDataRoot,
                profile,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_identity_invalid");
        }

        return new(
            request,
            manifest,
            association,
            roots,
            profile);
    }

    internal static WindowsMachineDeploymentPlan CreateFromWellKnownFolders(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        InstallerMachineAssociation association,
        string targetProfileRoot)
    {
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolderOption.DoNotVerify);
        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Create(
            request,
            manifest,
            association,
            programFiles,
            programData,
            targetProfileRoot);
    }

    internal void Validate()
    {
        Request.Validate();
        Manifest.Validate();
        Association.Validate();
        Roots.Validate();
        if (!string.Equals(Request.TargetSid, Association.OwnerSid, StringComparison.Ordinal)
            || PayloadTargets.Count != Manifest.MachineFiles.Count
            || PayloadTargets.Select(static target => target.Source.Path)
                .Distinct(StringComparer.Ordinal).Count() != Manifest.MachineFiles.Count
            || PayloadTargets.Select(static target => target.DestinationPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != Manifest.MachineFiles.Count)
        {
            throw new InstallerProtocolException(
                "installer.machine.deployment_plan_invalid");
        }

        foreach (WindowsMachinePayloadTarget target in PayloadTargets)
        {
            target.Validate(CurrentRoot);
        }

        WindowsMachinePayloadTarget host = PayloadTargets.Single(target =>
            string.Equals(
                target.Source.Path,
                "binaries/service/clashsharp.mihomoservice.exe",
                StringComparison.Ordinal));
        WindowsMachinePayloadTarget mihomo = PayloadTargets.Single(target =>
            string.Equals(
                target.Source.Path,
                "binaries/mihomo.exe",
                StringComparison.Ordinal));
        if (!string.Equals(host.DestinationPath, ServiceHostPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mihomo.DestinationPath, MihomoPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.deployment_plan_invalid");
        }

        RequireExactDescendant(
            ProgramFilesRoot,
            MachineRoot,
            "installer.machine.deployment_plan_invalid");
        RequireExactDescendant(
            MachineRoot,
            CurrentRoot,
            "installer.machine.deployment_plan_invalid");
        RequireExactDescendant(
            MachineRoot,
            StagingRoot,
            "installer.machine.deployment_plan_invalid");
        RequireExactDescendant(
            MachineRoot,
            PreviousRoot,
            "installer.machine.deployment_plan_invalid");
        if (string.Equals(CurrentRoot, StagingRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(CurrentRoot, PreviousRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StagingRoot, PreviousRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.deployment_plan_invalid");
        }

        RequireExactDescendant(
            CommonApplicationDataRoot,
            ServiceDataRoot,
            "installer.machine.deployment_plan_invalid");
        RequireExactDescendant(
            TargetProfileRoot,
            ConfigPath,
            "installer.machine.deployment_plan_invalid");
        Service.ValidateExpected();
    }

    internal static void RequireExactDescendant(
        string root,
        string candidate,
        string diagnosticCode)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidate);
        string prefix = string.Concat(fullRoot, Path.DirectorySeparatorChar);
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(diagnosticCode);
        }
    }

    private static IReadOnlyList<WindowsMachinePayloadTarget> BuildPayloadTargets(
        InstallerReleaseManifest manifest,
        string currentRoot)
    {
        var targets = new List<WindowsMachinePayloadTarget>(manifest.MachineFiles.Count);
        foreach (InstallerMachinePayloadFileEntry source in manifest.MachineFiles)
        {
            string relative = source.Path switch
            {
                "binaries/mihomo.exe" => "mihomo.exe",
                _ when source.Path.StartsWith(
                    "binaries/service/",
                    StringComparison.Ordinal) => Path.Combine(
                        "Host",
                        source.Path["binaries/service/".Length..]
                            .Replace('/', Path.DirectorySeparatorChar)),
                _ when source.Path.StartsWith(
                    "binaries/geodata/",
                    StringComparison.Ordinal) => Path.Combine(
                        "GeoData",
                        source.Path["binaries/geodata/".Length..]
                            .Replace('/', Path.DirectorySeparatorChar)),
                _ => throw new InstallerProtocolException(
                    "installer.machine.payload_target_invalid"),
            };
            string destination = Path.GetFullPath(Path.Combine(currentRoot, relative));
            var target = new WindowsMachinePayloadTarget(source, relative, destination);
            target.Validate(currentRoot);
            targets.Add(target);
        }

        return targets.ToArray();
    }

    private string BuildServiceBinaryPath()
    {
        string binaryPath = string.Join(
            ' ',
            Quote(ServiceHostPath),
            "--mihomo",
            Quote(MihomoPath),
            "--config",
            Quote(ConfigPath),
            "--pipe-name",
            Quote(Association.BuildServicePipeName()),
            "--ipc-token",
            Quote(Association.AuthenticationToken),
            "--allowed-sid",
            Quote(Association.OwnerSid));
        if (binaryPath.Length > MaximumServiceCommandCharacters)
        {
            throw new InstallerProtocolException(
                "installer.machine.service_command_too_long");
        }

        return binaryPath;
    }

    internal static string CanonicalRoot(string value, string diagnosticCode)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.AsSpan(2).Contains(':')
            || value.IndexOfAny(['*', '?', '<', '>', '|']) >= 0
            || value.Any(char.IsControl))
        {
            throw new InstallerProtocolException(diagnosticCode);
        }

        string canonical = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        string supplied = value.TrimEnd(Path.DirectorySeparatorChar);
        string? pathRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(pathRoot)
            || pathRoot.Length != 3
            || pathRoot[1] != ':'
            || !string.Equals(canonical, supplied, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(diagnosticCode);
        }

        return canonical;
    }

    internal static string Descendant(string root, params string[] segments)
    {
        string candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        RequireExactDescendant(
            root,
            candidate,
            "installer.machine.derived_path_invalid");
        return candidate;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new InstallerProtocolException(
                "installer.machine.service_argument_invalid");
        }

        return string.Concat('"', value, '"');
    }
}
