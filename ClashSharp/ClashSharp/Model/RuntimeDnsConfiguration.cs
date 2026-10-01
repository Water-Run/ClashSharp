using System;
using System.Collections.Immutable;

namespace ClashSharp.Model;

/// <summary>DNS values from one verified applied configuration; default arrays mean core defaults.</summary>
internal sealed record RuntimeDnsConfiguration(
    bool Enabled,
    string Mode,
    string Listen,
    bool Ipv6,
    bool RespectRules,
    ImmutableArray<string> NameServers,
    ImmutableArray<string> FallbackServers,
    ImmutableArray<string> BootstrapServers,
    ImmutableArray<string> DirectServers,
    ImmutableArray<string> ProxyServers,
    int PolicyCount)
{
    public bool Equals(RuntimeDnsConfiguration? other) => other is not null
        && Enabled == other.Enabled && Mode == other.Mode && Listen == other.Listen
        && Ipv6 == other.Ipv6 && RespectRules == other.RespectRules && PolicyCount == other.PolicyCount
        && SameServers(NameServers, other.NameServers) && SameServers(FallbackServers, other.FallbackServers)
        && SameServers(BootstrapServers, other.BootstrapServers) && SameServers(DirectServers, other.DirectServers)
        && SameServers(ProxyServers, other.ProxyServers);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Enabled);
        hash.Add(Mode, StringComparer.Ordinal);
        hash.Add(Listen, StringComparer.Ordinal);
        hash.Add(Ipv6);
        hash.Add(RespectRules);
        hash.Add(PolicyCount);
        foreach (ImmutableArray<string> servers in new[] { NameServers, FallbackServers, BootstrapServers, DirectServers, ProxyServers })
        {
            hash.Add(servers.IsDefault);
            if (!servers.IsDefault) { foreach (string server in servers) { hash.Add(server, StringComparer.Ordinal); } }
        }
        return hash.ToHashCode();
    }

    private static bool SameServers(ImmutableArray<string> left, ImmutableArray<string> right) =>
        left.IsDefault == right.IsDefault && (left.IsDefault || left.AsSpan().SequenceEqual(right.AsSpan()));
}
