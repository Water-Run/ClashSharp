using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.UI.Xaml;

namespace ClashSharp.Hosting.Startup;

/// <summary>Creates and activates the one primary application window.</summary>
internal sealed class WindowShellStartupStep(Action<Window> attachWindow) : IStartupStep
{
    public string Name => "window-shell";

    public int Order => 600;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MainWindow window = new();
        attachWindow(window);
        window.Activate();
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
