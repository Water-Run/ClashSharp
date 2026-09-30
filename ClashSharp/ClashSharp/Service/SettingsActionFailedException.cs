using System;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Diagnostics;

namespace ClashSharp.Service;

/// <summary>Reports an unverified runtime action without claiming that its durable desired value rolled back.</summary>
internal sealed class SettingsActionFailedException(SettingsAuthorityStatus status, string diagnosticCode)
    : InvalidOperationException($"The settings action failed: {status} ({diagnosticCode})."), IStableDiagnosticCodeProvider
{
    public SettingsAuthorityStatus Status { get; } = status;
    public string DiagnosticCode { get; } = diagnosticCode;
}
