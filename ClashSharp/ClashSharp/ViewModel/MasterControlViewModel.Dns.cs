using System;
using System.Collections.Immutable;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed partial class MasterControlViewModel
{
    private static readonly (string Id, string Key)[] DnsTiles =
    [
        ("dns-mode", "DnsMode"), ("dns-listen", "DnsListen"), ("dns-ipv6", "DnsIpv6"),
        ("dns-upstream", "DnsUpstream"), ("dns-fallback", "DnsFallback"), ("dns-bootstrap", "DnsBootstrap"),
    ];

    private void RefreshDnsTiles()
    {
        bool current = _runtimeSnapshot.IsAvailable && _runtimeSnapshot.RuntimeOwnershipKnown
            && StringComparer.Ordinal.Equals(_runtimeSnapshot.ActiveProfileId, _settings.ActiveProfileId);
        RuntimeDnsConfiguration? dns = current && _runtimeSnapshot.EffectiveOwner != MihomoCoreOwner.None
            ? _runtimeSnapshot.Dns : null;
        if (dns is null || !dns.Enabled)
        {
            string status = _localization.GetString(current && (_runtimeSnapshot.EffectiveOwner == MihomoCoreOwner.None || dns is { Enabled: false })
                ? "Master.Status.Off" : "Master.Status.Unavailable");
            foreach ((string id, string key) in DnsTiles)
            {
                SetTile(id, dns is { Enabled: false } && id == "dns-mode"
                    ? _localization.GetString("Master.Dns.System") : status, string.Empty, description: TileDescription(key));
            }
            return;
        }

        SetTile("dns-mode", dns.Mode,
            string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Dns.Routing.Format"),
                FormatSwitch(dns.RespectRules), FormatNumber(dns.PolicyCount)), description: TileDescription("DnsMode"));
        SetTile("dns-listen", string.IsNullOrEmpty(dns.Listen) ? _localization.GetString("Master.Dns.NotListening") : dns.Listen,
            string.Empty, description: TileDescription("DnsListen"));
        SetTile("dns-ipv6", FormatSwitch(dns.Ipv6), string.Empty, description: TileDescription("DnsIpv6"));
        string upstreamDescription = TileDescription("DnsUpstream") + Environment.NewLine
            + string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Dns.Direct.Format"), DnsServerDetail(dns.DirectServers))
            + Environment.NewLine
            + string.Format(CultureInfo.CurrentCulture, _localization.GetString("Master.Dns.Proxy.Format"), DnsServerDetail(dns.ProxyServers));
        SetDnsServerTile("dns-upstream", dns.NameServers, upstreamDescription);
        SetDnsServerTile("dns-fallback", dns.FallbackServers, TileDescription("DnsFallback"));
        SetDnsServerTile("dns-bootstrap", dns.BootstrapServers, TileDescription("DnsBootstrap"));
    }

    private void SetDnsServerTile(string id, ImmutableArray<string> servers, string description) =>
        SetTile(id, servers.IsDefault ? _localization.GetString("Master.Dns.CoreDefault") : FormatNumber(servers.Length),
            DnsServerDetail(servers), description: description);

    private string DnsServerDetail(ImmutableArray<string> servers) => servers.IsDefault
        ? _localization.GetString("Master.Dns.CoreDefault")
        : servers.IsEmpty ? _localization.GetString("Master.Dns.NotConfigured") : string.Join(" · ", servers);
}
