namespace ClashSharp.ViewModel;

/// <summary>Logging contract required by <see cref="ProxiesViewModel"/>.</summary>
internal interface IProxiesLog
{
    void Append(string level, string category, string message, string? detail);
}
