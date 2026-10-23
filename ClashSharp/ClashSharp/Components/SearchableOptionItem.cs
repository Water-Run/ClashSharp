using System;
using System.ComponentModel;

namespace ClashSharp.Components;

/// <summary>Bindable option state rendered by <see cref="SearchableOptionList"/>.</summary>
/// <remarks>
/// Invariants: Identity and payload are immutable; display refresh preserves the checked state.
/// Thread safety: Intended for the owning UI thread.
/// Side effects: Raises <see cref="PropertyChanged"/> when selection or display text changes.
/// </remarks>
public sealed class SearchableOptionItem : INotifyPropertyChanged
{
    private bool _isChecked;

    /// <summary>Initializes one selectable option.</summary>
    public SearchableOptionItem(
        string id,
        string title,
        string metadata,
        string description,
        string glyph,
        object? payload = null,
        bool isChecked = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Glyph = glyph ?? throw new ArgumentNullException(nameof(glyph));
        Payload = payload;
        _isChecked = isChecked;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the stable option identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the primary display text.</summary>
    public string Title { get; private set; }

    /// <summary>Gets compact secondary metadata.</summary>
    public string Metadata { get; private set; }

    /// <summary>Gets the explanatory display text.</summary>
    public string Description { get; private set; }

    /// <summary>Gets the Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; }

    /// <summary>Gets the caller-owned value associated with this option.</summary>
    public object? Payload { get; }

    /// <summary>Refreshes translated text in place without replacing this option or editing its selection.</summary>
    public void UpdateDisplay(string title, string metadata, string description)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(description);
        bool titleChanged = !StringComparer.Ordinal.Equals(Title, title);
        bool metadataChanged = !StringComparer.Ordinal.Equals(Metadata, metadata);
        bool descriptionChanged = !StringComparer.Ordinal.Equals(Description, description);
        Title = title;
        Metadata = metadata;
        Description = description;
        if (titleChanged) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
        if (metadataChanged) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metadata))); }
        if (descriptionChanged) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description))); }
    }

    /// <summary>Gets or sets whether the option is selected.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}
