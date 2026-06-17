/*
 * Core Configuration Service
 * Provides local mihomo configuration directory management and default configuration generation
 *
 * @author: WaterRun
 * @file: Service/CoreConfigurationService.cs
 * @date: 2026-06-17
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Manages local mihomo configuration paths and default configuration generation.</summary>
/// <remarks>
/// Invariants: The configuration directory is created before a default configuration is written.
/// Thread safety: Public mutation methods serialize filesystem access through a private lock.
/// Side effects: Creates directories and writes the local mihomo configuration file.
/// </remarks>
public sealed class CoreConfigurationService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="CoreConfigurationService"/> instance.</value>
    public static CoreConfigurationService Instance { get; } = new();

    /// <summary>Synchronization object guarding filesystem mutations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute directory path for mihomo runtime configuration.</summary>
    private readonly string _configurationDirectoryPath;

    /// <summary>Absolute file path for the generated mihomo configuration.</summary>
    private readonly string _configurationFilePath;

    /// <summary>Initializes the configuration service and resolves configuration paths.</summary>
    private CoreConfigurationService()
    {
        _configurationDirectoryPath = Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "mihomo");
        _configurationFilePath = Path.Combine(_configurationDirectoryPath, "config.yaml");
    }

    /// <summary>Gets the current local mihomo configuration state.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot for the managed configuration file.</returns>
    public CoreConfigurationState GetState()
    {
        return new CoreConfigurationState(
            _configurationDirectoryPath,
            _configurationFilePath,
            File.Exists(_configurationFilePath));
    }

    /// <summary>Ensures the local configuration directory and default configuration file exist.</summary>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    public CoreConfigurationState EnsureDefaultConfiguration()
    {
        return EnsureConfiguration(ClashSharpMode.Standby);
    }

    /// <summary>Ensures the local configuration directory and managed configuration file match <paramref name="mode"/>.</summary>
    /// <param name="mode">Master takeover mode whose mihomo mode should be represented in the generated configuration.</param>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> cannot be mapped to a mihomo mode.</exception>
    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode)
    {
        return EnsureConfiguration(
            mode,
            MihomoRuntimeConfigurationBuilder.ShouldEnableTransparentProxy(mode, AppSettingsService.Instance.TransparentProxyEnabled));
    }

    /// <summary>Ensures the local configuration directory and managed configuration file match <paramref name="mode"/> and transparent proxy preference.</summary>
    /// <param name="mode">Master takeover mode whose mihomo mode should be represented in the generated configuration.</param>
    /// <param name="transparentProxyEnabled">True to enable mihomo TUN transparent proxy configuration.</param>
    /// <returns>A <see cref="CoreConfigurationState"/> snapshot after the ensure operation completes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> cannot be mapped to a mihomo mode.</exception>
    public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled)
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_configurationDirectoryPath);

            string configText = BuildRuntimeConfiguration(AppSettingsService.Instance.MixedPort, mode, transparentProxyEnabled);
            File.WriteAllText(_configurationFilePath, configText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return GetState();
        }
    }

    /// <summary>Downloads have already completed; validates and imports profile configuration text into the managed profile store.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null or whitespace.</param>
    /// <param name="profileName">User-facing profile name. Must not be null or whitespace.</param>
    /// <param name="configurationText">Downloaded mihomo configuration text. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels external mihomo validation.</param>
    /// <returns>Import result containing profile path and estimated counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/>, <paramref name="profileName"/>, or <paramref name="configurationText"/> is null.</exception>
    /// <exception cref="ArgumentException">A required argument is whitespace or the configuration does not look like a mihomo profile.</exception>
    /// <exception cref="InvalidOperationException">Bundled mihomo rejects the imported configuration.</exception>
    public async Task<ProfileImportResult> ImportProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(profileName);
        ArgumentNullException.ThrowIfNull(configurationText);

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile identifier must not be whitespace.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("Profile name must not be whitespace.", nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(configurationText))
        {
            throw new ArgumentException("Profile configuration must not be whitespace.", nameof(configurationText));
        }

        string normalizedProfileId = NormalizeProfileId(profileId);
        string normalizedText = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(configurationText);
        MihomoProfileShapeValidator.Validate(normalizedText);

        string profileDirectory = GetProfileDirectoryPath(normalizedProfileId);
        string profileConfigPath = Path.Combine(profileDirectory, "config.yaml");
        string backupPath = Path.Combine(profileDirectory, "config.yaml.bak");
        int nodeCount = MihomoProfileParserService.Instance.ParseNodes(normalizedText).Count;
        int ruleCount = MihomoProfileParserService.Instance.ParseRules(normalizedText).Count;

        lock (_syncLock)
        {
            Directory.CreateDirectory(profileDirectory);
            if (File.Exists(profileConfigPath))
            {
                File.Copy(profileConfigPath, backupPath, overwrite: true);
            }

            File.WriteAllText(profileConfigPath, normalizedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        try
        {
            await ValidateWithBundledMihomoAsync(profileDirectory, profileConfigPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestoreProfileBackup(profileConfigPath, backupPath);
            throw;
        }

        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        return new ProfileImportResult(
            normalizedProfileId,
            profileName.Trim(),
            profileConfigPath,
            nodeCount,
            ruleCount,
            "配置已下载、校验并导入。");
    }

    /// <summary>Returns the imported profile configuration path for <paramref name="profileId"/>.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null.</param>
    /// <returns>Absolute imported profile configuration path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/> is null.</exception>
    public string GetProfileConfigurationPath(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return Path.Combine(GetProfileDirectoryPath(NormalizeProfileId(profileId)), "config.yaml");
    }

    /// <summary>Validates an already-imported profile configuration with the bundled mihomo binary when available.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels external mihomo validation.</param>
    /// <returns>Import-style profile metrics for the validated configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="profileId"/> is whitespace or the configuration shape is invalid.</exception>
    /// <exception cref="FileNotFoundException">The imported profile configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Bundled mihomo rejects the imported configuration.</exception>
    public async Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile identifier must not be whitespace.", nameof(profileId));
        }

        string normalizedProfileId = NormalizeProfileId(profileId);
        string profileDirectory = GetProfileDirectoryPath(normalizedProfileId);
        string profileConfigPath = Path.Combine(profileDirectory, "config.yaml");
        if (!File.Exists(profileConfigPath))
        {
            throw new FileNotFoundException("Imported profile configuration was not found.", profileConfigPath);
        }

        string configurationText = MihomoRuntimeConfigurationBuilder.NormalizeConfigurationText(
            await File.ReadAllTextAsync(profileConfigPath, cancellationToken).ConfigureAwait(false));
        MihomoProfileShapeValidator.Validate(configurationText);
        await ValidateWithBundledMihomoAsync(profileDirectory, profileConfigPath, cancellationToken).ConfigureAwait(false);

        return new ProfileImportResult(
            normalizedProfileId,
            normalizedProfileId,
            profileConfigPath,
            MihomoProfileParserService.Instance.ParseNodes(configurationText).Count,
            MihomoProfileParserService.Instance.ParseRules(configurationText).Count,
            "配置校验通过。");
    }

    /// <summary>Builds runtime configuration from the active imported profile when available, otherwise from the default profile.</summary>
    /// <param name="mixedPort">Mixed HTTP and SOCKS proxy port in range [1, 65535].</param>
    /// <param name="mode">Master takeover mode whose equivalent mihomo mode should be emitted.</param>
    /// <returns>Runtime configuration text with deterministic line endings.</returns>
    private string BuildRuntimeConfiguration(int mixedPort, ClashSharpMode mode, bool transparentProxyEnabled)
    {
        string activeProfileId = AppSettingsService.Instance.ActiveProfileId;
        string profileConfigPath = GetProfileConfigurationPath(activeProfileId);
        if (File.Exists(profileConfigPath) && !StringComparer.Ordinal.Equals(activeProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            string profileText = File.ReadAllText(profileConfigPath);
            return MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(profileText, mixedPort, mode, transparentProxyEnabled);
        }

        return MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(mixedPort, mode, transparentProxyEnabled);
    }

    /// <summary>Runs bundled mihomo configuration validation when the binary is available.</summary>
    /// <param name="workingDirectory">Validation working directory. Must not be null.</param>
    /// <param name="configurationPath">Configuration file path. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the validation process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workingDirectory"/> or <paramref name="configurationPath"/> is null.</exception>
    /// <exception cref="InvalidOperationException">mihomo exits with a non-zero code.</exception>
    private static async Task ValidateWithBundledMihomoAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(configurationPath);

        MihomoCoreService coreService = MihomoCoreService.Instance;
        if (!coreService.IsBinaryAvailable)
        {
            return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = coreService.BinaryPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(workingDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configurationPath);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start bundled mihomo for configuration validation.");
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"mihomo rejected the imported configuration: {detail}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    /// <summary>Restores the previous imported profile configuration after failed validation.</summary>
    /// <param name="profileConfigPath">Current profile configuration path. Must not be null.</param>
    /// <param name="backupPath">Backup path. Must not be null.</param>
    private static void RestoreProfileBackup(string profileConfigPath, string backupPath)
    {
        ArgumentNullException.ThrowIfNull(profileConfigPath);
        ArgumentNullException.ThrowIfNull(backupPath);

        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, profileConfigPath, overwrite: true);
            File.Delete(backupPath);
            return;
        }

        if (File.Exists(profileConfigPath))
        {
            File.Delete(profileConfigPath);
        }
    }

    /// <summary>Normalizes a profile identifier so it is safe for a local directory name.</summary>
    /// <param name="profileId">Profile identifier. Must not be null.</param>
    /// <returns>Filesystem-safe profile identifier; never null.</returns>
    private static string NormalizeProfileId(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        StringBuilder builder = new();
        foreach (char character in profileId.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "profile" : builder.ToString();
    }

    /// <summary>Returns the profile directory for <paramref name="profileId"/>.</summary>
    /// <param name="profileId">Filesystem-safe profile identifier. Must not be null.</param>
    /// <returns>Absolute profile directory path.</returns>
    private string GetProfileDirectoryPath(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return Path.Combine(_configurationDirectoryPath, "profiles", profileId);
    }

    /// <summary>Attempts to terminate <paramref name="process"/> after validation cancellation.</summary>
    /// <param name="process">Process to terminate. Must not be null.</param>
    private static void TryKillProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

}
