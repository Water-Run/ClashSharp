using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClashSharp.MihomoService;

/// <summary>Starts and supervises mihomo for the Windows service host.</summary>
internal sealed class MihomoWorker : BackgroundService
{
    /// <summary>Writes a structured restart warning without per-call template parsing.</summary>
    private static readonly Action<ILogger, int, Exception?> LogMihomoExited = LoggerMessage.Define<int>(
        LogLevel.Warning,
        new EventId(1, nameof(LogMihomoExited)),
        "mihomo exited with code {ExitCode}; restarting.");

    /// <summary>Writes a structured process-start message without per-call template parsing.</summary>
    private static readonly Action<ILogger, Exception?> LogMihomoStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(2, nameof(LogMihomoStarted)),
        "mihomo started as service child process.");

    /// <summary>Parsed service options.</summary>
    private readonly MihomoServiceOptions _options;

    /// <summary>Logger.</summary>
    private readonly ILogger<MihomoWorker> _logger;

    /// <summary>Currently running mihomo process.</summary>
    private Process? _process;

    /// <summary>Initializes a worker.</summary>
    public MihomoWorker(MihomoServiceOptions options, ILogger<MihomoWorker> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs mihomo until the service stops.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ValidateFiles();

        while (!stoppingToken.IsCancellationRequested)
        {
            using Process process = StartMihomo();
            _process = process;
            try
            {
                await process.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                StopProcess(process);
                break;
            }
            finally
            {
                _process = null;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                LogMihomoExited(_logger, process.ExitCode, null);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    /// <summary>Stops the child process when the service is stopping.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            StopProcess(_process);
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>Validates configured files before starting mihomo.</summary>
    private void ValidateFiles()
    {
        if (!File.Exists(_options.MihomoPath))
        {
            throw new FileNotFoundException("mihomo executable was not found.", _options.MihomoPath);
        }

        if (!File.Exists(_options.ConfigPath))
        {
            throw new FileNotFoundException("mihomo configuration was not found.", _options.ConfigPath);
        }

        Directory.CreateDirectory(_options.WorkDirectory);
    }

    /// <summary>Starts mihomo with configured paths.</summary>
    private Process StartMihomo()
    {
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.MihomoPath,
                WorkingDirectory = _options.WorkDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add(_options.WorkDirectory);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(_options.ConfigPath);
        process.Start();
        LogMihomoStarted(_logger, null);
        return process;
    }

    /// <summary>Stops a running process tree.</summary>
    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsExpectedProcessTerminationException(exception))
        {
        }
    }

    private static bool IsExpectedProcessTerminationException(Exception exception)
    {
        return exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException;
    }
}
