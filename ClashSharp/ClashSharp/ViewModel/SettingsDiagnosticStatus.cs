using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Status update returned to the settings page after a diagnostic command.</summary>
/// <param name="Target">Diagnostic target whose visible status should be updated.</param>
/// <param name="Message">Status message to display.</param>
internal readonly record struct SettingsDiagnosticStatus(WindowsDiagnosticTarget Target, string Message);
