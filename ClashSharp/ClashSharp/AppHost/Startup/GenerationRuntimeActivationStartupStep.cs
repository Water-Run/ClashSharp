using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;

namespace ClashSharp.Hosting.Startup;

/// <summary>Applies startup network policy and releases owned producers only after conflict checks.</summary>
internal sealed class GenerationRuntimeActivationStartupStep(DataGenerationStartupStep data,
    StartupConflictSnapshot conflicts, Func<ClashSharpMode, CancellationToken, Task> publishMode) : IStartupStep
{
    public string Name => "startup-network-behavior";
    public int Order => 450;

    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken) =>
        data.ActivateRuntimeAsync(conflicts, publishMode, cancellationToken);
}
