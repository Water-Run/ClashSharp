namespace ClashSharp.Hosting.Compatibility;

/// <summary>Cached trigger counts consumed by presentation surfaces that still use WinUI activation.</summary>
internal sealed record TriggerPresentationSummary(int TaskCount, int EnabledTaskCount);
