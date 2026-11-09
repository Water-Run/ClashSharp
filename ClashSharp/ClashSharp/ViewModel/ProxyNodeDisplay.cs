using System;
using System.Globalization;
using System.Linq;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a proxy node.</summary>
internal sealed class ProxyNodeDisplay(ProxyNode model, string nameDisplay) : ObservableObject
{
    private string _latencyDisplay = "—";
    public ProxyNode Model { get; } = model;
    public string NameDisplay { get; } = nameDisplay;
    public string LatencyDisplay { get => _latencyDisplay; internal set => SetProperty(ref _latencyDisplay, value); }

    /// <summary>Formats a TCP measurement without presenting direct routes or unavailable endpoints as measured latency.</summary>
    internal static string FormatLatency(ProxyNode node, Func<string, string> getString)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(node.Protocol, "DIRECT"))
        {
            return getString("ProxyNodes.Latency.Direct");
        }
        if (node.LatencyMilliseconds is int latency)
        {
            return string.Format(CultureInfo.CurrentCulture, getString("Master.Status.Latency.Format"), latency);
        }
        return getString(!node.WasLatencyTested
            ? "Master.Status.LatencyUnavailable"
            : string.IsNullOrWhiteSpace(node.ServerHost) || node.ServerPort is null
                ? "ProxyNodes.Latency.NoEndpoint"
                : "ProxyNodes.Latency.Failed");
    }

    public string Protocol => Model.Protocol;

    public RegionMetadata Region => Model.Region;

    public int? LatencyMilliseconds => Model.LatencyMilliseconds;

    /// <summary>Gives screen readers a meaningful row name; the default would be the CLR type name.</summary>
    public override string ToString() => string.Join(", ", new[] { NameDisplay, Protocol, LatencyDisplay }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
