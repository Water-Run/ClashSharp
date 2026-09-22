extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;
using CoreConfigurationState = ClashSharpUi::ClashSharp.Model.CoreConfigurationState;
using IProfileCatalogAdmittedSettings = ClashSharpUi::ClashSharp.Service.IProfileCatalogAdmittedSettings;
using IProfileCatalogCoreConfiguration = ClashSharpUi::ClashSharp.Service.IProfileCatalogCoreConfiguration;
using IProfileCatalogLog = ClashSharpUi::ClashSharp.Service.IProfileCatalogLog;
using IProfileCatalogRuntime = ClashSharpUi::ClashSharp.Service.IProfileCatalogRuntime;
using IProfileCatalogSettings = ClashSharpUi::ClashSharp.Service.IProfileCatalogSettings;
using LogStorageService = ClashSharpUi::ClashSharp.Service.LogStorageService;
using LogStorageServiceFactory = ClashSharpUi::ClashSharp.Service.LogStorageServiceFactory;
using ProfileCatalogIds = ClashSharpUi::ClashSharp.Service.ProfileCatalogIds;
using ProfileCatalogMutationCoordinator = ClashSharpUi::ClashSharp.Service.ProfileCatalogMutationCoordinator;
using ProfileCatalogRuntimeImportResult = ClashSharpUi::ClashSharp.Service.ProfileCatalogRuntimeImportResult;
using ProfileCatalogService = ClashSharpUi::ClashSharp.Service.ProfileCatalogService;
using ProfileCatalogServiceFactory = ClashSharpUi::ClashSharp.Service.ProfileCatalogServiceFactory;
using ProfileImportResult = ClashSharpUi::ClashSharp.Model.ProfileImportResult;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises AppHost disposal against the actual main-program repository implementations.</summary>
public sealed class ProductionRepositoryLifetimeTests
{
    [Fact]
    public async Task HostDisposal_DrainsActualCatalogThenRetiresActualLogStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "clashsharp-owned-repositories-" + Guid.NewGuid().ToString("N"));
        LogStorageService? logs = null;
        ProfileCatalogService? profiles = null;
        Settings settings = new();
        Runtime runtime = new();
        AppHost host = AppHost.Build(services =>
        {
            services.AddSingleton(_ => logs = LogStorageServiceFactory.CreateForDirectory(root, () => settings.ActiveProfileId));
            services.AddSingleton(provider => profiles = ProfileCatalogServiceFactory.CreateForDirectory(
                root, settings, new Configuration(), runtime,
                new Log(provider.GetRequiredService<LogStorageService>()), key => key,
                new ProfileCatalogMutationCoordinator(new MutationAdmissionBarrier(), new FairAsyncMutationGate())));
            services.AddSingleton<IApplicationStartupCoordinator>(provider => new Startup(provider.GetRequiredService<ProfileCatalogService>()));
        });
        try
        {
            Assert.False(Directory.Exists(root));
            _ = await host.StartAsync(new AppLaunchRequest(string.Empty), CancellationToken.None);
            Assert.NotNull(profiles);
            Assert.NotNull(logs);
            logs.AppendLog("Info", "Startup", "before retirement", null);
            Task<bool> activation = profiles.TryApplyActiveProfileAsync(ProfileCatalogIds.BuiltInDirect, CancellationToken.None);
            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task retiringHost = host.DisposeAsync().AsTask();
            try
            {
                Assert.False(retiringHost.IsCompleted);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => profiles.AddSubscriptionLinkAsync("late", "https://example.test/late", CancellationToken.None));
                // The catalog still owns an accepted operation, so its log dependency must remain available.
                logs.AppendLog("Info", "Catalog", "finishing accepted operation", null);
            }
            finally
            {
                runtime.Release.TrySetResult(true);
            }

            Assert.True(await activation);
            await retiringHost.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => logs.AppendLog("Info", "late", "late", null));
            Assert.Throws<ObjectDisposedException>(() => profiles.GetProfiles());
            await using LogStorageService reopened = LogStorageServiceFactory.CreateForDirectory(root, () => settings.ActiveProfileId);
            Assert.Equal(2, reopened.GetRecentLogs(10).Count);
            Assert.Equal(ProfileCatalogIds.BuiltInDirect, settings.ActiveProfileId);
        }
        finally
        {
            runtime.Release.TrySetResult(true);
            await host.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private sealed class Startup(ProfileCatalogService profiles) : IApplicationStartupCoordinator
    {
        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            _ = profiles.GetProfiles();
            return Task.FromResult(StartupStepResult.Succeeded());
        }
    }

    private sealed class Settings : IProfileCatalogSettings, IProfileCatalogAdmittedSettings
    {
        public string ActiveProfileId { get; set; } = ProfileCatalogIds.BuiltInDirect;

        public void SetActiveProfileAdmitted(MutationAdmissionLease admissionLease, string profileId) => ActiveProfileId = profileId;
    }

    private sealed class Log(LogStorageService logs) : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail) => logs.AppendLog(level, category, message, detail);
    }

    private sealed class Runtime : IProfileCatalogRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Release.Task;
        }

        public Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Configuration : IProfileCatalogCoreConfiguration
    {
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(string profileId, string profileName, string configurationText, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ReadImportedProfileConfigurationAsync(string profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public CoreConfigurationState EnsureDefaultConfiguration() => throw new NotSupportedException();
        public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
