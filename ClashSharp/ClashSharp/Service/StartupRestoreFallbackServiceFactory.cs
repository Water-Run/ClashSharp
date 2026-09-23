namespace ClashSharp.Service;

public sealed partial class StartupRestoreFallbackService
{
    /// <summary>Gets the current user's independent packaged restore-task service.</summary>
    public static StartupRestoreFallbackService Instance { get; } =
        new(StartupLaunchServiceFactory.CreateDefault(TaskId));
}
