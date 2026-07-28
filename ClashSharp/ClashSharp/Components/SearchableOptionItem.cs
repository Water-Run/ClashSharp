using System;
using System.ComponentModel;

namespace ClashSharp.Components;

/// <summary>Bindable option state rendered by <see cref="SearchableOptionList"/>.</summary>
/// <remarks>
/// Invariants: Identity and display metadata are immutable; checked state raises change notifications.
/// Thread safety: Intended for the owning UI thread.
/// Side effects: Raises <see cref="PropertyChanged"/> when selection changes.
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
    public string Title { get; }

    /// <summary>Gets compact secondary metadata.</summary>
    public string Metadata { get; }

    /// <summary>Gets the explanatory display text.</summary>
    public string Description { get; }

    /// <summary>Gets the Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; }

    /// <summary>Gets the caller-owned value associated with this option.</summary>
    public object? Payload { get; }

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
