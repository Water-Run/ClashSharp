namespace ClashSharp.ViewModel;

/// <summary>Diagnostic log boundary used by presentation view models.</summary>
internal interface IPageLog
{
    /// <summary>Appends one complete diagnostic entry.</summary>
    void Append(string level, string category, string message, string? detail);
}
