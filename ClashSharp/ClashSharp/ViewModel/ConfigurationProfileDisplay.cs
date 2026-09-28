using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a configuration profile.</summary>
internal sealed class ConfigurationProfileDisplay(
    ConfigurationProfile model,
    string nameDisplay,
    string sourceNameDisplay,
    string statusDisplay) : ObservableObject
{
    public ConfigurationProfile Model { get; private set; } = model;

    public string NameDisplay { get; private set; } = nameDisplay;

    public string SourceNameDisplay { get; private set; } = sourceNameDisplay;

    public string StatusDisplay { get; private set; } = statusDisplay;

    /// <summary>Updates a retained row without discarding its selection, focus, or list position.</summary>
    public void UpdateFrom(ConfigurationProfileDisplay updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (!StringComparer.Ordinal.Equals(Id, updated.Id))
        {
            throw new ArgumentException("Profile identity must remain unchanged.", nameof(updated));
        }

        if (Model == updated.Model && NameDisplay == updated.NameDisplay
            && SourceNameDisplay == updated.SourceNameDisplay && StatusDisplay == updated.StatusDisplay)
        {
            return;
        }

        Model = updated.Model;
        NameDisplay = updated.NameDisplay;
        SourceNameDisplay = updated.SourceNameDisplay;
        StatusDisplay = updated.StatusDisplay;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(NameDisplay));
        OnPropertyChanged(nameof(SourceNameDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(RuleCount));
        OnPropertyChanged(nameof(IsActive));
    }

    public string Id => Model.Id;

    public DateTimeOffset UpdatedAt => Model.UpdatedAt;

    public int NodeCount => Model.NodeCount;

    public int RuleCount => Model.RuleCount;

    public bool IsActive => Model.IsActive;
}
