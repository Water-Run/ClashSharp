using System;
using System.Windows.Input;

namespace ClashSharp.ViewModel;

/// <summary>One draggable master-control information tile.</summary>
internal sealed class MasterControlInfoTileViewModel : ObservableObject
{
    private string _value;
    private string _detail;
    private bool _isVisible = true;
    private bool _isToggleOn;

    public MasterControlInfoTileViewModel(
        string id,
        string title,
        string value,
        string detail,
        string glyph,
        string description,
        string typeText,
        bool isToggleVisible = false,
        bool isToggleOn = false,
        ICommand? tileCommand = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _value = value ?? throw new ArgumentNullException(nameof(value));
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Glyph = glyph ?? throw new ArgumentNullException(nameof(glyph));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        TypeText = typeText ?? throw new ArgumentNullException(nameof(typeText));
        IsToggleVisible = isToggleVisible;
        _isToggleOn = isToggleOn;
        TileCommand = tileCommand;
    }

    public string Id { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string Description { get; }

    public string TypeText { get; }

    public bool IsToggleVisible { get; }

    public ICommand? TileCommand { get; }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsToggleOn
    {
        get => _isToggleOn;
        set => SetProperty(ref _isToggleOn, value);
    }
}
