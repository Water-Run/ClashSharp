using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

internal sealed class MasterHeroStatusItemViewModel : ObservableObject
{
    private MasterHeroStatusItemKind _kind;
    private string _title;
    private string _value;

    public MasterHeroStatusItemViewModel(MasterHeroStatusItemKind kind, string title, string value)
    {
        _kind = kind;
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public MasterHeroStatusItemKind Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}
