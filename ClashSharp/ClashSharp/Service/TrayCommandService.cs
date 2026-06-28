/*
 * Tray Command Service
 * Coordinates task tray commands with runtime mode, transparent proxy preference, and logging
 *
 * @author: WaterRun
 * @file: Service/TrayCommandService.cs
 * @date: 2026-06-25
 */

using System;
using System.ComponentModel;
using System.IO;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Settings contract required by <see cref="TrayCommandService"/>.</summary>
internal interface ITrayCommandSettings
{
    ClashSharpMode CurrentMode { get; set; }

    bool TransparentProxyEnabled { get; set; }
}

/// <summary>Mihomo service status contract required by <see cref="TrayCommandService"/>.</summary>
internal interface ITrayCommandMihomoService
{
    MihomoServiceStatus GetStatus();
}

/// <summary>Network takeover contract required by <see cref="TrayCommandService"/>.</summary>
internal interface ITrayCommandTakeover
{
    NetworkTakeoverResult ApplyMode(ClashSharpMode mode);
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

    private readonly ITrayCommandSettings _settings;
    private readonly ITrayCommandMihomoService _mihomoService;
    private readonly ITrayCommandTakeover _takeover;
    private readonly ITrayCommandLog _log;

    public TrayCommandService(
        ITrayCommandSettings settings,
        ITrayCommandMihomoService mihomoService,
        ITrayCommandTakeover takeover,
        ITrayCommandLog log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mihomoService = mihomoService ?? throw new ArgumentNullException(nameof(mihomoService));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool ApplyMode(ClashSharpMode mode)
    {
        return TryApplyMode(mode, "Tray mode change failed.");
    }

    public bool SetTransparentProxyEnabled(bool isEnabled)
    {
        MihomoServiceStatus serviceStatus = _mihomoService.GetStatus();
        if (isEnabled && !serviceStatus.IsInstalled)
        {
            _settings.TransparentProxyEnabled = true;
            return false;
        }

        _settings.TransparentProxyEnabled = isEnabled;
        if (_settings.CurrentMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover)
        {
            return TryApplyMode(_settings.CurrentMode, "Tray transparent proxy change failed.");
        }

        return false;
    }

    private bool TryApplyMode(ClashSharpMode mode, string failureMessage)
    {
        try
        {
            NetworkTakeoverResult result = _takeover.ApplyMode(mode);
            _settings.CurrentMode = result.Mode;
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
