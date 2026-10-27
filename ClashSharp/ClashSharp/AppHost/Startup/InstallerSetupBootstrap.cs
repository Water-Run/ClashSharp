using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Settings;
using Windows.ApplicationModel;
using Windows.Storage;

namespace ClashSharp.Hosting.Startup;

/// <summary>Applies only declared current-package login tasks; no host, network core or main window is constructed.</summary>
internal static class InstallerSetupBootstrap
{
    internal static async Task<int?> TryRunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 0 || arguments[0] != InstallationSetupProtocol.BootstrapArgument) { return null; }
        if (arguments.Length != 2 || !Guid.TryParseExact(arguments[1], "N", out Guid id)
            || id == Guid.Empty || id.ToString("N") != arguments[1]) { return 2; }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = ApplicationData.Current.LocalSettings.Values;
            if (!values.TryGetValue(InstallationSetupProtocol.RequestKey, out object? raw)
                || raw is not ApplicationDataCompositeValue request || request.Count != 5
                || !request.TryGetValue("schema", out object? schema) || schema is not 1
                || !request.TryGetValue("id", out object? requestId) || requestId is not string text || text != arguments[1]
                || !request.TryGetValue("startup", out object? startup) || startup is not bool startupEnabled
                || !request.TryGetValue("recovery", out object? recovery) || recovery is not bool recoveryEnabled
                || !request.TryGetValue("applyDefaults", out object? apply) || apply is not bool applyDefaults)
            {
                return 2;
            }
            int warnings = 0;
            if (applyDefaults)
            {
                if (!await SetTaskAsync("ClashSharpStartup", startupEnabled, cancellationToken)) { warnings |= 1; }
                if (!await SetTaskAsync("ClashSharpProxyRestoreFallback", recoveryEnabled, cancellationToken)) { warnings |= 2; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.TryGetValue(InstallationSetupProtocol.RequestKey, out object? current)
                || current is not ApplicationDataCompositeValue owned || owned.Count != request.Count
                || request.Any(pair => !owned.TryGetValue(pair.Key, out object? value) || !Equals(value, pair.Value)))
            {
                return 2;
            }
            values[InstallationSetupProtocol.ReceiptKey] = new ApplicationDataCompositeValue
            {
                ["schema"] = 1,
                ["id"] = arguments[1],
                ["warnings"] = warnings,
            };
            values.Remove(InstallationSetupProtocol.RequestKey);
            return 0;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // Stable process failure only. A parent-owned pending intent remains available for retry.
            return 3;
        }
    }

    private static async Task<bool> SetTaskAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await InstallerStartupTaskSetup.ApplyAsync(enabled,
                async () => new NativeStartupTask(await StartupTask.GetAsync(id)), cancellationToken);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        { return false; }
    }

    private sealed class NativeStartupTask(StartupTask task) : IInstallerStartupTask
    {
        public StartupTaskState State => task.State;
        public void Disable() => task.Disable();
        public async Task EnableAsync() => _ = await task.RequestEnableAsync();
    }
}
