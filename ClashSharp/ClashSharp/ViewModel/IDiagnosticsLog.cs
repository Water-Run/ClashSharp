namespace ClashSharp.ViewModel;

/// <summary>Writes diagnostic command logs for the settings diagnostics view model.</summary>
internal interface IDiagnosticsLog
{
    void Append(string level, string category, string message, string? detail);
}
