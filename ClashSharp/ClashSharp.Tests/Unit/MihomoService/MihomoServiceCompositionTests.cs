using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Exercises the real service container without starting Windows services or child processes.</summary>
public sealed class MihomoServiceCompositionTests
{
    /// <summary>Resolves the complete hosted-service graph while leaving runtime storage untouched.</summary>
    [Fact]
    public async Task ProductionContainerResolvesWithoutRuntimeSideEffects()
    {
        using var directory = new MihomoServiceTemporaryDirectory();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(directory.Path);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMihomoServiceRuntime(options);

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        Assert.IsType<MihomoWorker>(Assert.Single(provider.GetServices<IHostedService>()));
        MihomoChildSupervisor supervisor = provider.GetRequiredService<MihomoChildSupervisor>();
        Assert.Same(supervisor, provider.GetRequiredService<MihomoChildSupervisor>());
        Assert.Equal(MihomoServiceChildState.Stopped, supervisor.GetSnapshot().ChildState);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
}
