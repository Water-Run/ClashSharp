using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

public sealed partial class MihomoServiceManager
{
    /// <summary>Reads one bounded, service-redacted host-log snapshot.</summary>
    internal async Task<IReadOnlyList<string>> ReadHostLogsAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfServiceNotProvisioned();
        MihomoServiceIpcRequest request = new()
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = _ipcEndpoint.AuthenticationToken,
            Command = MihomoServiceIpcCommand.Logs,
            MaximumLogEntries = MihomoServiceIpcProtocol.MaximumLogEntries,
        };
        MihomoServiceIpcResponse response = await SendIpcRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Succeeded)
        {
            throw new MihomoServiceCommandException(
                response.ErrorCode ?? "service.ipc.command_failed");
        }

        return [.. response.Logs];
    }
}
