/*
 * Mihomo Core Service
 * Provides binary discovery and runtime probing for the bundled mihomo core
 *
 * @author: WaterRun
 * @file: Service/MihomoCoreService.cs
 * @date: 2026-06-15
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides access to the bundled mihomo core binary and its runtime process state.</summary>
/// <remarks>
/// Invariants: <see cref="BinaryPath"/> always points to the expected bundled core location.
/// Thread safety: Public process state reads are guarded by a private lock.
/// Side effects: Version probing starts a short-lived mihomo process.
/// </remarks>
public sealed class MihomoCoreService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoCoreService"/> instance.</value>
    public static MihomoCoreService Instance { get; } = new();

    /// <summary>Synchronization object guarding mutable process state for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Current long-running mihomo process owned by Clash#; null when not started.</summary>
    private Process? _process = null;

    /// <summary>Initializes the core service and resolves the bundled binary path.</summary>
    private MihomoCoreService()
    {
        BinaryPath = Path.Combine(AppContext.BaseDirectory, "Binaries", "mihomo.exe");
    }

    /// <summary>Gets the expected bundled mihomo binary path.</summary>
    /// <value>Absolute path under the application base directory; never null.</value>
    public string BinaryPath { get; }

    /// <summary>Gets whether the bundled mihomo binary currently exists on disk.</summary>
    /// <value>True when <see cref="BinaryPath"/> exists; otherwise false.</value>
    public bool IsBinaryAvailable => File.Exists(BinaryPath);

    /// <summary>Gets whether a long-running mihomo process owned by this service is currently active.</summary>
    /// <value>True when the owned process exists and has not exited; otherwise false.</value>
    public bool IsRunning
    {
        get
        {
            lock (_syncLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Runs the bundled mihomo binary with the version flag and returns its first output line.</summary>
    /// <param name="cancellationToken">Cancels the probe and terminates the probe process when cancellation is requested.</param>
    /// <returns>The first non-empty version output line from mihomo.</returns>
    /// <exception cref="FileNotFoundException">The bundled mihomo binary does not exist at <see cref="BinaryPath"/>.</exception>
    /// <exception cref="InvalidOperationException">The version probe cannot start or exits without version output.</exception>
    /// <remarks>
    /// Cancellation semantics: Cancellation terminates only the short-lived probe process.
    /// Completion semantics: The method starts a new process on each call and does not mutate long-running core state.
    /// </remarks>
    public async Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
    {
        if (!IsBinaryAvailable)
        {
            throw new FileNotFoundException("Bundled mihomo core was not found.", BinaryPath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = BinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the bundled mihomo core.");
        }

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();
            string text = string.IsNullOrWhiteSpace(output) ? error : output;
            string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return lines.Length > 0
                ? lines[0]
                : throw new InvalidOperationException("The bundled mihomo core exited without version output.");
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    /// <summary>Starts the bundled mihomo core with the managed local configuration when it is not already running.</summary>
    /// <param name="configurationState">Managed configuration state whose file must exist.</param>
    /// <exception cref="FileNotFoundException">The bundled core binary or configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    public void Start(CoreConfigurationState configurationState)
    {
        if (!IsBinaryAvailable)
        {
            throw new FileNotFoundException("Bundled mihomo core was not found.", BinaryPath);
        }

        if (!configurationState.Exists)
        {
            throw new FileNotFoundException("Managed mihomo configuration was not found.", configurationState.ConfigPath);
        }

        lock (_syncLock)
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = BinaryPath,
                WorkingDirectory = configurationState.DirectoryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(configurationState.DirectoryPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(configurationState.ConfigPath);

            Process process = new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Unable to start the bundled mihomo core.");
            }

            _process = process;
        }
    }

    /// <summary>Restarts the bundled mihomo core with <paramref name="configurationState"/>.</summary>
    /// <param name="configurationState">Managed configuration state whose file must exist.</param>
    /// <exception cref="FileNotFoundException">The bundled core binary or configuration file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The core process cannot be started.</exception>
    public void Restart(CoreConfigurationState configurationState)
    {
        Stop();
        Start(configurationState);
    }

    /// <summary>Stops the owned mihomo core process when it is running.</summary>
    public void Stop()
    {
        Process? processToStop;

        lock (_syncLock)
        {
            processToStop = _process;
            _process = null;
        }

        if (processToStop is null)
        {
            return;
        }

        using (processToStop)
        {
            TryKillProcess(processToStop);
        }
    }

    /// <summary>Attempts to terminate <paramref name="process"/> when a probe is canceled.</summary>
    /// <param name="process">Process instance to terminate. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="process"/> is null.</exception>
    private static void TryKillProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
    }
}
