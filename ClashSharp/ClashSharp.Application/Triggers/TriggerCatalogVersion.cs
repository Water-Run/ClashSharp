namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies one definition revision in one data directory.</summary>
/// <param name="DataGenerationId">Managed data identity, or null for a single unscoped repository.</param>
/// <param name="Generation">Optimistic definition revision within that directory.</param>
public readonly record struct TriggerCatalogVersion(Guid? DataGenerationId, long Generation);
