namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Refreshes visible language-dependent text without reloading or writing user state.</summary>
/// <remarks>Called on the UI thread after the applied language changes. Drafts, selection and operation ownership remain intact.</remarks>
public interface ILocalizedPresentation
{
    /// <summary>Reprojects current presentation text using the applied language.</summary>
    void RefreshLocalization();
}
