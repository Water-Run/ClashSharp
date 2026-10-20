using System;
using System.Windows.Input;

namespace ClashSharp.ViewModel;

/// <summary>One draggable master-control information tile.</summary>
internal sealed class MasterControlInfoTileViewModel : ObservableObject
{
    private string _value;
    private string _detail;
    private string _description;
    private string _title;
    private string _typeText;
    private string _reorderHint = string.Empty;
    private string _navigationText = string.Empty;
    private bool _isVisible = true;
    private bool _isToggleOn;

    private double[] _history = [];

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
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _value = value ?? throw new ArgumentNullException(nameof(value));
        _detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Glyph = glyph ?? throw new ArgumentNullException(nameof(glyph));
        _description = description ?? throw new ArgumentNullException(nameof(description));
        _typeText = typeText ?? throw new ArgumentNullException(nameof(typeText));
        IsToggleVisible = isToggleVisible;
        _isToggleOn = isToggleOn;
        TileCommand = tileCommand;
    }

    public string Id { get; }

    public string Title => _title;

    public string Glyph { get; }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string TypeText => _typeText;

    public string ReorderHint { get => _reorderHint; init => _reorderHint = value; }

    public bool IsToggleVisible { get; }

    public ICommand? TileCommand { get; }

    /// <summary>Optional explicit page navigation, separate from opening the full details.</summary>
    public ICommand? NavigationCommand { get; init; }

    public string NavigationText { get => _navigationText; init => _navigationText = value; }

    /// <summary>Updates translated text while retaining tile identity, layout, commands and history.</summary>
    public void RefreshLocalization(string title, string description, string typeText, string reorderHint, string navigationText)
    {
        SetProperty(ref _title, title, nameof(Title));
        Description = description;
        SetProperty(ref _typeText, typeText, nameof(TypeText));
        SetProperty(ref _reorderHint, reorderHint, nameof(ReorderHint));
        SetProperty(ref _navigationText, navigationText, nameof(NavigationText));
    }

    public double[] History
    {
        get => _history;
        set => SetProperty(ref _history, value);
    }

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
        internal set => SetProperty(ref _isVisible, value);
    }

    public bool IsToggleOn
    {
        get => _isToggleOn;
        set => SetProperty(ref _isToggleOn, value);
    }
}
