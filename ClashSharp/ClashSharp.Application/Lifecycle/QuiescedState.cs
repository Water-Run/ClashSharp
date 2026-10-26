namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Opaque participant state captured before a completed or interrupted quiescence attempt.</summary>
/// <param name="WasRunning">Whether the participant was actively scheduling work before it paused.</param>
public sealed record QuiescedState(bool WasRunning);
