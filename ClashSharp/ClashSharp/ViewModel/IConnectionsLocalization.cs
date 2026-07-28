namespace ClashSharp.ViewModel;

/// <summary>Localization contract required by <see cref="ConnectionsViewModel"/>.</summary>
internal interface IConnectionsLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    string GetString(string key);
}
