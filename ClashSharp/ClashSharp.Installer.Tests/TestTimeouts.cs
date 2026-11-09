namespace ClashSharp.Installer.Tests;

/// <summary>
/// Deadlines for waits that exist only to turn a hang into a failure.
/// </summary>
/// <remarks>
/// A completion wait is not a performance assertion: the awaited work either finishes or never will. A short
/// wall-clock budget makes the verdict depend on the machine (a shared runner can stall for many seconds, for
/// example on durable file writes), so these waits use one generous deadline. Waits that assert something does
/// <em>not</em> happen yet are state checks and never use this value.
/// </remarks>
internal static class TestTimeouts
{
    /// <summary>The longest a completion wait may take before the test is considered hung.</summary>
    internal static readonly TimeSpan Completion = TimeSpan.FromSeconds(60);
}
