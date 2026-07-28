namespace ClashSharp.ViewModel;

/// <summary>Logging contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlLog
{
    /// <summary>Appends one log entry.</summary>
    void Append(string level, string category, string message, string? detail);
}
