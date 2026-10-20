namespace ClashSharp.ViewModel;

/// <summary>One localized option whose typed value is interpreted only by an editor ViewModel.</summary>
internal sealed class TriggerEditorOption<T>(T value, string title, string description) : ObservableObject
{
    private string _title = title;
    private string _description = description;
    public T Value { get; } = value;
    public string Title => _title;
    public string Description => _description;

    internal void RefreshLocalization(string title, string description)
    {
        SetProperty(ref _title, title, nameof(Title));
        SetProperty(ref _description, description, nameof(Description));
    }
}
