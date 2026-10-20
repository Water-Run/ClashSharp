using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Connection-test report containing all target rows and a localized summary.</summary>
internal sealed record ConnectionTestReport(
    IReadOnlyList<ConnectionTestTargetResult> Results,
    string SummaryText,
    ConnectionTestSummaryState SummaryState)
{
    internal LocalizedDisplayText? SummarySource { get; init; }
}
