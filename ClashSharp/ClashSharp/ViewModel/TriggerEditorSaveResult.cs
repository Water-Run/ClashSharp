using System;

namespace ClashSharp.ViewModel;

/// <summary>Typed completion returned by the list owner after an editor save request.</summary>
internal sealed record TriggerEditorSaveResult(bool IsSucceeded, string? ErrorCode)
{
    public static TriggerEditorSaveResult Succeeded() => new(true, null);

    public static TriggerEditorSaveResult Failed(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new TriggerEditorSaveResult(false, errorCode);
    }
}
