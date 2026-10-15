namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Accesses application-owned light and dark accent overrides on their owning UI thread, preserving system contrast themes.</summary>
public interface IAccentResourceStore
{
    /// <summary>Captures both normal-theme values; absent keys are omitted and incomplete or incompatible pairs are represented by null.</summary>
    /// <param name="ownedKeys">The complete set of keys owned by accent application.</param>
    /// <returns>A stable snapshot of owned entries, excluding fallback framework resources.</returns>
    IReadOnlyDictionary<string, AccentResourceValue?> CaptureLocalOverrides(IReadOnlyCollection<string> ownedKeys);

    /// <summary>Installs both theme values of one exact owned color or solid-brush resource without changing contrast-theme resources.</summary>
    /// <param name="key">The exact owned resource key.</param>
    /// <param name="value">The requested color and resource kind.</param>
    void WriteOverride(string key, AccentResourceValue value);

    /// <summary>Removes one owned override from both normal themes while preserving system and contrast-theme resources.</summary>
    /// <param name="key">The exact owned resource key.</param>
    void RemoveOverride(string key);
}
