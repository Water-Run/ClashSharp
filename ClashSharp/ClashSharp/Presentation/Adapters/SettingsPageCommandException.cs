using System;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Retains a value-free authority failure for presentation while desired values remain separately observable.</summary>
internal sealed class SettingsPageCommandException(SettingsAuthorityResult result)
    : InvalidOperationException($"The settings command failed: {result.Status} ({result.Code}).")
{
    public SettingsAuthorityStatus Status { get; } = result.Status;
    public string? Code { get; } = result.Code;
}
