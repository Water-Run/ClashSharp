using System;

namespace ClashSharp.ViewModel;

/// <summary>Minimal localization contract required by the main shell view model.</summary>
internal interface IShellLocalization
{
    /// <summary>Occurs when localized strings should be refreshed.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Gets a localized string for the supplied key.</summary>
    string GetString(string key);
}
