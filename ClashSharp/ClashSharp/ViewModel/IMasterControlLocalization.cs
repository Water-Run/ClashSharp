namespace ClashSharp.ViewModel;

/// <summary>Localization contract required by <see cref="MasterControlViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every requested key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IMasterControlLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    string GetString(string key);
}
