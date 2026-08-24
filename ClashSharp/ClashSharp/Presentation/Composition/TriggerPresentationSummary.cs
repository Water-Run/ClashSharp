namespace ClashSharp.Presentation.Composition;

/// <summary>Cached trigger counts consumed by presentation surfaces.</summary>
internal sealed record TriggerPresentationSummary(int TaskCount, int EnabledTaskCount);
