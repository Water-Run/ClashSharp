using System;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.ViewModel;

/// <summary>One immutable failed application presented with its exact recovery identity.</summary>
internal sealed record SettingsApplicationIssue(string Title, string RetryText, SettingsAuthoritySnapshot Snapshot, Guid BatchId);
