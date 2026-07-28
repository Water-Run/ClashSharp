using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a configuration profile.</summary>
internal sealed record ConfigurationProfileDisplay(
    ConfigurationProfile Model,
    string NameDisplay,
    string SourceNameDisplay,
    string StatusDisplay)
{
    public string Id => Model.Id;

    public DateTimeOffset UpdatedAt => Model.UpdatedAt;

    public int NodeCount => Model.NodeCount;

    public int RuleCount => Model.RuleCount;

    public bool IsActive => Model.IsActive;
}
