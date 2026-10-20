using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClashSharp.ViewModel;

/// <summary>Retains completed probe facts while refreshing only their presentation.</summary>
internal sealed class ConnectionTestDisplay : ObservableObject
{
    private readonly ConnectionTestReport _report;
    private readonly Func<string, string> _getString;

    public ConnectionTestDisplay(ConnectionTestReport report, Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        Rows = report.Results.Select(result => new ConnectionTestTargetDisplay(result, getString)).ToArray();
    }

    public IReadOnlyList<ConnectionTestTargetDisplay> Rows { get; }
    public string SummaryText => _report.SummarySource?.Resolve(_getString) ?? _report.SummaryText;
    public ConnectionTestSummaryState SummaryState => _report.SummaryState;

    public void RefreshLocalization()
    {
        foreach (ConnectionTestTargetDisplay row in Rows) { row.RefreshLocalization(); }
        OnPropertyChanged(nameof(SummaryText));
    }
}

/// <summary>One immutable URL/result identity with display-only language and number formatting.</summary>
internal sealed class ConnectionTestTargetDisplay(ConnectionTestTargetResult model, Func<string, string> getString) : ObservableObject
{
    public ConnectionTestTargetResult Model { get; } = model;
    public string Label => Model.LabelSource?.Resolve(getString) ?? Model.Label;
    public string Url => Model.Url;
    public bool Succeeded => Model.Succeeded;
    public string StatusText => Model.StatusSource?.Resolve(getString) ?? Model.StatusText;
    public string LatencyText => Model.ObservedElapsedMilliseconds is int elapsed
        ? elapsed.ToString(CultureInfo.CurrentCulture) + " ms" : Model.LatencyText;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LatencyText));
    }
}
