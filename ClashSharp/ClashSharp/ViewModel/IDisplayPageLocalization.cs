using System;

namespace ClashSharp.ViewModel;

/// <summary>Localization contract shared by read-oriented display page view models.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IDisplayPageLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    string GetString(string key);
}
