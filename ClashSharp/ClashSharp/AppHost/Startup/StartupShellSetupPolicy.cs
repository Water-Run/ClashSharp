using System;
using System.Diagnostics.CodeAnalysis;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting.Startup;

/// <summary>Contains recoverable failures from optional primary-window setup.</summary>
internal static class StartupShellSetupPolicy
{
    /// <summary>Runs optional shell setup without hiding a usable standard window on ordinary failure.</summary>
    /// <param name="setup">One independent, non-critical setup operation.</param>
    /// <returns><see langword="true"/> when setup completed; otherwise, <see langword="false"/>.</returns>
    internal static bool TryRun(Action setup)
    {
        ArgumentNullException.ThrowIfNull(setup);

        try
        {
            setup();
            return true;
        }
        catch (Exception exception) when (StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            // The initialized XAML window is the startup fallback; optional decoration is best effort.
            return false;
        }
    }

    /// <summary>Runs optional shell setup that produces a value.</summary>
    /// <typeparam name="T">Produced value type.</typeparam>
    /// <param name="setup">One independent, non-critical setup operation.</param>
    /// <param name="result">Produced value when setup completes.</param>
    /// <returns><see langword="true"/> when setup completed; otherwise, <see langword="false"/>.</returns>
    internal static bool TryRun<T>(
        Func<T> setup,
        [MaybeNullWhen(false)] out T result)
    {
        ArgumentNullException.ThrowIfNull(setup);

        try
        {
            result = setup();
            return true;
        }
        catch (Exception exception) when (StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            result = default;
            return false;
        }
    }
}
