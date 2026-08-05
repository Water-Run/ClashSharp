using System.Security.Principal;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.MihomoService;

/// <summary>Validated command-line options for one installed mihomo service endpoint.</summary>
internal sealed class MihomoServiceOptions
{
    private static readonly string[] RequiredOptionNames =
    [
        "--mihomo",
        "--config",
        "--pipe-name",
        "--ipc-token",
        "--allowed-sid",
    ];

    internal MihomoServiceOptions(
        string mihomoPath,
        string configPath,
        string pipeName,
        string ipcToken,
        SecurityIdentifier allowedSid,
        string? serviceDataDirectory = null)
    {
        MihomoPath = NormalizeAbsolutePath(mihomoPath, nameof(mihomoPath));
        ConfigPath = NormalizeAbsolutePath(configPath, nameof(configPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(allowedSid);
        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(ipcToken))
        {
            throw new ArgumentException(
                "The IPC token must be canonical lowercase SHA-256 text.",
                nameof(ipcToken));
        }

        string expectedPipeName = MihomoServiceIpcProtocol.BuildPipeName(allowedSid.Value, ipcToken);
        if (!string.Equals(pipeName, expectedPipeName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The pipe name does not match the authenticated owner endpoint.",
                nameof(pipeName));
        }

        RejectPrivilegedOrRemotePrincipal(allowedSid);
        PipeName = pipeName;
        IpcToken = ipcToken;
        AllowedSid = allowedSid;
        ServiceDataDirectory = NormalizeAbsolutePath(
            serviceDataDirectory ?? BuildServiceDataDirectory(pipeName),
            nameof(serviceDataDirectory));
    }

    public string MihomoPath { get; }

    public string ConfigPath { get; }

    public string PipeName { get; }

    public string IpcToken { get; }

    public SecurityIdentifier AllowedSid { get; }

    /// <summary>Gets the LocalSystem-owned root used for immutable generation staging.</summary>
    public string ServiceDataDirectory { get; }

    /// <summary>Gets the LocalSystem-owned mihomo working directory.</summary>
    public string RuntimeDirectory => Path.Combine(ServiceDataDirectory, "runtime");

    /// <summary>Gets the installer-owned geodata bundle located next to mihomo.</summary>
    public string GeoDataDirectory => Path.Combine(
        Path.GetDirectoryName(MihomoPath)
            ?? throw new InvalidOperationException("The mihomo installation directory is unavailable."),
        "GeoData");

    /// <summary>Parses exactly one value for every supported service option.</summary>
    public static MihomoServiceOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(
                    $"Missing value for service option {args[index]}.",
                    nameof(args));
            }

            string name = args[index];
            if (!RequiredOptionNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unknown service option {name}.", nameof(args));
            }

            string value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Empty value for service option {name}.", nameof(args));
            }

            if (!values.TryAdd(name, value))
            {
                throw new ArgumentException($"Duplicate service option {name}.", nameof(args));
            }
        }

        foreach (string name in RequiredOptionNames)
        {
            if (!values.ContainsKey(name))
            {
                throw new ArgumentException($"Missing required option {name}.", nameof(args));
            }
        }

        SecurityIdentifier allowedSid;
        try
        {
            allowedSid = new SecurityIdentifier(values["--allowed-sid"]);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The allowed SID is invalid.", nameof(args), exception);
        }

        if (!string.Equals(
                allowedSid.Value,
                values["--allowed-sid"],
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The allowed SID is not canonical.", nameof(args));
        }

        return new MihomoServiceOptions(
            values["--mihomo"],
            values["--config"],
            values["--pipe-name"],
            values["--ipc-token"],
            allowedSid);
    }

    /// <summary>Returns a diagnostic description that deliberately excludes the deployment token.</summary>
    public override string ToString()
    {
        return $"PipeName={PipeName}, AllowedSid={AllowedSid.Value}, "
            + $"MihomoPath={MihomoPath}, ConfigPath={ConfigPath}, RuntimeDirectory={RuntimeDirectory}";
    }

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Service paths must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static string BuildServiceDataDirectory(string pipeName)
    {
        string commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(commonApplicationData))
        {
            throw new InvalidOperationException("The common application-data directory is unavailable.");
        }

        return Path.Combine(commonApplicationData, "ClashSharp", "MihomoService", pipeName);
    }

    private static void RejectPrivilegedOrRemotePrincipal(SecurityIdentifier sid)
    {
        WellKnownSidType[] forbidden =
        [
            WellKnownSidType.WorldSid,
            WellKnownSidType.AnonymousSid,
            WellKnownSidType.NetworkSid,
            WellKnownSidType.LocalSystemSid,
            WellKnownSidType.BuiltinAdministratorsSid,
        ];
        if (forbidden.Any(sid.IsWellKnown))
        {
            throw new ArgumentException(
                "The allowed SID must identify the target interactive user.",
                nameof(sid));
        }
    }
}
