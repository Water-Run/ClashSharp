namespace ClashSharp.ViewModel;

/// <summary>One localized option whose typed value is interpreted only by an editor ViewModel.</summary>
internal sealed record TriggerEditorOption<T>(T Value, string Title, string Description);
