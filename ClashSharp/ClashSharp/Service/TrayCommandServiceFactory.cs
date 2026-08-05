using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates application-wired tray command services.</summary>
internal static class TrayCommandServiceFactory
{
    public static TrayCommandService CreateDefault(ApplicationActionService actions)
    {
        return new TrayCommandService(
            new TrayCommandTakeoverAdapter(actions),
            new TrayCommandLogAdapter(LogStorageService.Instance));
    }
}

/// <summary>Adapts network takeover service to tray commands.</summary>
internal sealed class TrayCommandTakeoverAdapter(ApplicationActionService actions) : ITrayCommandTakeover
{
    public Task<NetworkTakeoverResult> ApplyModeAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        return actions.ApplyNetworkModeAsync(mode, cancellationToken);
    }

    public Task<NetworkTakeoverResult> ApplyTransparentProxyAsync(
        bool transparentProxyEnabled,
        CancellationToken cancellationToken)
    {
        return actions.ApplyTransparentProxyAsync(
            transparentProxyEnabled,
            cancellationToken);
    }
}

/// <summary>Adapts log storage to tray command logging.</summary>
internal sealed class TrayCommandLogAdapter(LogStorageService log) : ITrayCommandLog
{
    public void Append(string level, string category, string message, string? detail)
    {
        log.AppendLog(level, category, message, detail);
    }
}
