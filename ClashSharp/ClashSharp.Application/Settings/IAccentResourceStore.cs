namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Accesses the primary application dictionary's accent overrides on its owning UI thread.</summary>
public interface IAccentResourceStore
{
    /// <summary>Captures locally defined owned keys; absent keys are omitted and incompatible resource values are represented by null.</summary>
    /// <param name="ownedKeys">The complete set of keys owned by accent application.</param>
    /// <returns>A stable snapshot that excludes inherited and merged resources.</returns>
    IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys);

    /// <summary>Installs one exact owned color or solid-brush resource.</summary>
    /// <param name="key">The exact owned resource key.</param>
    /// <param name="value">The requested color and resource kind.</param>
    void WriteOverride(string key, AccentResourceValue value);

    /// <summary>Removes one primary-dictionary override while preserving merged system resources.</summary>
    /// <param name="key">The exact owned resource key.</param>
    void RemoveOverride(string key);
}
