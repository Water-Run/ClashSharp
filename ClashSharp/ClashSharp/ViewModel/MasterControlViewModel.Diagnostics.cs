using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Infrastructure.Networking;

namespace ClashSharp.ViewModel;

internal sealed partial class MasterControlViewModel
{
    private readonly Func<string, CancellationToken, Task<WebsiteProbeResult>>? _probeWebsiteAsync;
    private readonly Func<CancellationToken, Task<PublicIpInformation>>? _probePublicIpAsync;
    private IReadOnlyList<WebsiteProbeResult> _websiteResults = [];
    private PublicIpInformation? _publicIp;
    private string? _ipFailure;
    private string _diagnosticContext = string.Empty;
    private DateTimeOffset? _websitesCheckedAt;
    private DateTimeOffset? _ipCheckedAt;
    private bool _checkingWebsites;
    private bool _checkingIp;
    private readonly IApplicationUpdateChecker? _updateChecker;
    private ApplicationUpdateCheckResult? _updateResult;
    private DateTimeOffset? _updateCheckedAt;
    private bool _checkingUpdate;

    private string UpdateStatus => _checkingUpdate ? _localization.GetString("About.Update.Checking")
        : _updateResult?.Availability switch
        {
            ApplicationUpdateAvailability.Current => _localization.GetString("About.Update.Current"),
            ApplicationUpdateAvailability.UpdateAvailable => string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("About.Update.Available.Format"), _updateResult.LatestVersion),
            ApplicationUpdateAvailability.Unavailable => _localization.GetString("About.Update.Unavailable"),
            _ => _localization.GetString("Master.Diagnostics.NotTested"),
        };

    public string UpdateDetails => $"{UpdateStatus}\n{_updateCheckedAt?.ToLocalTime():G}\nGitHub Releases · Water-Run/ClashSharp";

    /// <summary>Checks release availability and records the time of this explicit check.</summary>
    public async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_checkingUpdate || _updateChecker is null) { return; }
        _checkingUpdate = true;
        _updateResult = null;
        _updateCheckedAt = null;
        RefreshTileValues();
        try
        {
            ApplicationUpdateCheckResult result = await _updateChecker.CheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _updateResult = result;
            _updateCheckedAt = _getNow();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _updateResult = ApplicationUpdateCheckResult.Unavailable();
            _updateCheckedAt = _getNow();
        }
        finally
        {
            _checkingUpdate = false;
            RefreshTileValues();
        }
    }

    public string WebsiteDetails => _websiteResults.Count == 0 ? _localization.GetString("Master.Diagnostics.NotTested")
        : string.Join("\n\n", _websiteResults.Select(result => $"{result.Url}\n{FormatWebsiteResult(result)}"));

    public string PublicIpDetails => _publicIp is PublicIpInformation ip
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Ip.Details.Format"),
            ip.Address, IpField(ip.Location), IpField(ip.Asn), IpField(ip.Isp), IpField(ip.Organization), IpField(ip.Timezone),
            _ipCheckedAt?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture))
        : _localization.GetString(_ipFailure ?? "Master.Diagnostics.NotTested");

    private string IpField(string value) => string.IsNullOrWhiteSpace(value)
        ? _localization.GetString("Master.Status.Unavailable") : value;

    /// <summary>Checks all configured destinations, retaining HTTP failures and timeouts as distinct results.</summary>
    public async Task CheckWebsitesAsync(CancellationToken cancellationToken)
    {
        if (_checkingWebsites || _probeWebsiteAsync is null) { return; }
        RefreshNetworkDiagnosticTiles();
        string context = _diagnosticContext;
        string[] urls = [_settings.ConnectionTestProxyUrl1, _settings.ConnectionTestProxyUrl2, _settings.ConnectionTestDirectUrl];
        _checkingWebsites = true;
        RefreshNetworkDiagnosticTiles();
        try
        {
            WebsiteProbeResult[] results = await Task.WhenAll(urls.Select(url => _probeWebsiteAsync(url, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            if (context == GetDiagnosticContext())
            {
                _websiteResults = results;
                _websitesCheckedAt = _getNow();
            }
        }
        finally
        {
            _checkingWebsites = false;
            RefreshNetworkDiagnosticTiles();
        }
    }

    /// <summary>Queries public egress metadata on demand and discards results if the selected route changes.</summary>
    public async Task RefreshPublicIpAsync(CancellationToken cancellationToken)
    {
        if (_checkingIp || _probePublicIpAsync is null) { return; }
        RefreshNetworkDiagnosticTiles();
        string context = _diagnosticContext;
        _checkingIp = true;
        _publicIp = null;
        _ipFailure = null;
        RefreshNetworkDiagnosticTiles();
        try
        {
            PublicIpInformation result = await _probePublicIpAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (context == GetDiagnosticContext())
            {
                _publicIp = result;
                _ipCheckedAt = _getNow();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            if (context == GetDiagnosticContext())
            {
                _ipFailure = exception is OperationCanceledException ? "Master.Diagnostics.Timeout" : "Master.Diagnostics.Failed";
            }
        }
        finally
        {
            _checkingIp = false;
            RefreshNetworkDiagnosticTiles();
        }
    }

    private string GetDiagnosticContext() => string.Join("\n", _settings.CurrentMode, _settings.TransparentProxyEnabled,
        _settings.ActiveProfileId, _settings.MixedPort, _systemProxyAddress, CurrentNodeText,
        _settings.ConnectionTestProxyUrl1, _settings.ConnectionTestProxyUrl2, _settings.ConnectionTestDirectUrl);

    private void RefreshNetworkDiagnosticTiles()
    {
        string context = GetDiagnosticContext();
        if (!StringComparer.Ordinal.Equals(context, _diagnosticContext))
        {
            _diagnosticContext = context;
            _websiteResults = [];
            _publicIp = null;
            _ipFailure = null;
            _websitesCheckedAt = null;
            _ipCheckedAt = null;
        }

        string untested = _localization.GetString("Master.Diagnostics.NotTested");
        string testing = _localization.GetString("Master.Diagnostics.Testing");
        string networkRoute = _localization.GetString("Master.Diagnostics.CurrentRoute");
        SetTile("connection-test", _checkingWebsites ? testing : _websiteResults.Count == 0 ? untested
            : string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Diagnostics.Passed.Format"),
                _websiteResults.Count(static result => result.StatusCode is >= 200 and < 400), _websiteResults.Count),
            _websitesCheckedAt is DateTimeOffset checkedAt ? $"{networkRoute} · {checkedAt.ToLocalTime():T}" : networkRoute);
        (string Id, string Url)[] targets =
        [
            ("connection-test-proxy-url-1", _settings.ConnectionTestProxyUrl1),
            ("connection-test-proxy-url-2", _settings.ConnectionTestProxyUrl2),
            ("connection-test-direct-url", _settings.ConnectionTestDirectUrl),
        ];
        for (int index = 0; index < targets.Length; index++)
        {
            (string id, string url) = targets[index];
            SetTile(id, _checkingWebsites ? testing : index < _websiteResults.Count ? FormatWebsiteResult(_websiteResults[index]) : CompactUrl(url),
                $"{url}\n{networkRoute}");
        }

        SetTile("public-ip", _checkingIp ? testing : _publicIp?.Address ?? _localization.GetString(_ipFailure ?? "Master.Diagnostics.NotTested"),
            _publicIp is null ? _localization.GetString("Master.Tile.Description.PublicIp") : PublicIpDetails);
    }

    private string FormatWebsiteResult(WebsiteProbeResult result) => result.StatusCode is int status
        ? $"HTTP {status} · {result.LatencyMilliseconds} ms"
        : _localization.GetString(result.Failure == "timeout" ? "Master.Diagnostics.Timeout" : "Master.Diagnostics.Failed");
}
