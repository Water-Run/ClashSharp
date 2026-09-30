using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Data;

namespace ClashSharp.Hosting.Startup;

/// <summary>Installs repository ports before recovery can construct native factory singletons.</summary>
internal sealed class RuntimeDataBindingStartupStep(RuntimeDataBinding binding) : IStartupStep
{
    private readonly RuntimeDataBinding _binding = binding;
    public string Name => "runtime-data-binding";
    public int Order => 25;
    public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _binding;
        return Task.FromResult(StartupStepResult.Succeeded());
    }
}
