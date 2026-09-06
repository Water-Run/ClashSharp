namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Exposes execution state to presentation controls without depending on a command implementation.</summary>
public interface IAsyncCommandState
{
    /// <summary>Gets whether the command currently owns an unfinished invocation.</summary>
    bool IsRunning { get; }
}
