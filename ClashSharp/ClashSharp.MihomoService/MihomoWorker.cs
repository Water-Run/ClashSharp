using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClashSharp.MihomoService;

/// <summary>Hosts IPC only; the authenticated command supervisor exclusively owns child lifecycle.</summary>
internal sealed class MihomoWorker : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogServerStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1, nameof(LogServerStarted)),
        "Mihomo service IPC server started; child remains command-controlled.");

    private readonly MihomoServicePipeServer _server;
    private readonly MihomoChildSupervisor _supervisor;
    private readonly ILogger<MihomoWorker> _logger;

    public MihomoWorker(
        MihomoServicePipeServer server,
        MihomoChildSupervisor supervisor,
        ILogger<MihomoWorker> logger)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServerStarted(_logger, null);
        return _server.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _supervisor.ShutdownAsync().ConfigureAwait(false);
        }
    }
}
