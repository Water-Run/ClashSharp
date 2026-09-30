using System.Diagnostics.CodeAnalysis;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Registers startup metadata separately from services that earlier recovery steps must initialize.</summary>
public static class StartupServiceCollectionExtensions
{
    /// <summary>Resolves a host-owned singleton step only when its ordered execution begins.</summary>
    /// <typeparam name="TStep">Concrete startup step whose lifetime is owned by the host provider.</typeparam>
    /// <param name="services">Host registrations; an existing concrete step factory is preserved.</param>
    /// <param name="name">Stable diagnostic name, verified against the resolved step before execution.</param>
    /// <param name="order">Execution order, verified against the resolved step before execution.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddDeferredStartupStep<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStep>(
        this IServiceCollection services,
        string name,
        int order) where TStep : class, IStartupStep
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.TryAddSingleton<TStep>();
        services.AddSingleton<IStartupStep>(provider => new DeferredStartupStep(
            name, order, () => provider.GetRequiredService<TStep>()));
        return services;
    }

    private sealed class DeferredStartupStep(string name, int order, Func<IStartupStep> resolve) : IStartupStep
    {
        public string Name => name;
        public int Order => order;

        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            IStartupStep step = resolve();
            if (step.Name != Name || step.Order != Order)
            {
                throw new InvalidOperationException($"Startup step '{Name}' does not match its registered identity.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return step.ExecuteAsync(request, cancellationToken);
        }
    }
}
