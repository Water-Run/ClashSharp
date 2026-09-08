using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Applies all selected participants and verifies that they preserve the authoritative snapshot.</summary>
internal static class SettingsRuntimeApplier
{
    internal static async Task<Exception?> TryApplyAsync(
        ISettingsRuntimeParticipants operation,
        SettingsRuntimeSnapshot snapshot,
        SettingsResetScope scope)
    {
        List<Exception> failures = [];
        if (scope == SettingsResetScope.All)
        {
            Capture(() => operation.ApplyLanguage(snapshot.DisplayLanguage), failures);
            Capture(() => operation.ApplyTheme(snapshot.AppThemeMode), failures);
            Capture(() => operation.ApplyAccentColor(snapshot.AppAccentColorMode, snapshot.AppAccentColorValue), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Startup)
        {
            await CaptureAsync(
                () => operation.ApplyLaunchAtStartupAsync(snapshot.LaunchAtStartupEnabled, CancellationToken.None), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Proxy)
        {
            await CaptureAsync(() => operation.RestartConnectionSamplingAsync(CancellationToken.None), failures);
        }

        if (scope is SettingsResetScope.All or SettingsResetScope.Proxy or SettingsResetScope.TransparentProxy)
        {
            await CaptureAsync(
                () => operation.ApplyNetworkSettingsAsync(snapshot.TransparentProxyEnabled, snapshot.MixedPort, CancellationToken.None), failures);
        }

        Capture(() =>
        {
            if (operation.CaptureSnapshot() != snapshot)
            {
                throw new InvalidOperationException(
                    "A settings participant did not preserve the durable external settings snapshot.");
            }
        }, failures);
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("One or more settings participants failed to apply the durable state.", failures),
        };
    }

    private static void Capture(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureAsync(Func<Task> action, ICollection<Exception> failures)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }
    }
}
