using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Network takeover contract required by <see cref="TrayCommandService"/>.</summary>
internal interface ITrayCommandTakeover
{
    Task<NetworkTakeoverResult> ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken);

    Task<NetworkTakeoverResult> ApplyTransparentProxyAsync(
        bool transparentProxyEnabled,
        CancellationToken cancellationToken);
}

/// <summary>Logging contract required by <see cref="TrayCommandService"/>.</summary>
internal interface ITrayCommandLog
{
    void Append(string level, string category, string message, string? detail);
}

/// <summary>Coordinates task tray commands without depending on WinUI objects.</summary>
internal sealed class TrayCommandService
{
    private const string LogCategory = "Tray";

    private readonly ITrayCommandTakeover _takeover;
    private readonly ITrayCommandLog _log;

    public TrayCommandService(
        ITrayCommandTakeover takeover,
        ITrayCommandLog log)
    {
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<bool> ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken)
    {
        return TryApplyModeAsync(mode, "Tray mode change failed.", cancellationToken);
    }

    public Task<bool> SetTransparentProxyEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        return TryApplyNetworkSettingsAsync(isEnabled, cancellationToken);
    }

    private async Task<bool> TryApplyNetworkSettingsAsync(
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            NetworkTakeoverResult result = await _takeover
                .ApplyTransparentProxyAsync(isEnabled, cancellationToken)
                .ConfigureAwait(false);
            _log.Append("Info", LogCategory, result.Message, null);
            return true;
        }
        catch (Exception exception) when (IsExpectedTakeoverFailure(exception))
        {
            _log.Append("Error", LogCategory, "Tray transparent proxy change failed.", exception.Message);
            return false;
        }
    }

    private async Task<bool> TryApplyModeAsync(
        ClashSharpMode mode,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            NetworkTakeoverResult result = await _takeover
                .ApplyModeAsync(mode, cancellationToken)
                .ConfigureAwait(false);
            _log.Append("Info", LogCategory, result.Message, null);
            return true;
        }
        catch (Exception exception) when (IsExpectedTakeoverFailure(exception))
        {
            _log.Append("Error", LogCategory, failureMessage, exception.Message);
            return false;
        }
    }

    private static bool IsExpectedTakeoverFailure(Exception exception)
    {
        return exception is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or Win32Exception;
    }
}
