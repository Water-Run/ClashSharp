namespace ClashSharp.ViewModel;

/// <summary>One connection-test target result ready for dialog presentation.</summary>
internal sealed record ConnectionTestTargetResult(
    string Label,
    string Url,
    bool Succeeded,
    string StatusText,
    string LatencyText,
    int? LatencyMilliseconds);
