namespace ClashSharp.ViewModel;

/// <summary>Connection logging contract used by <see cref="ConnectionsViewModel"/>.</summary>
internal interface IConnectionLog
{
    void Append(string level, string category, string message, string? detail);
}
