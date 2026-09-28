using System.Text;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class ClashDataPackageServiceTests
{
    [Theory]
    [InlineData(ClashDataPackageScope.Settings)]
    [InlineData(ClashDataPackageScope.SettingsAndProxyConfiguration)]
    public async Task CoordinatedExport_WaitsForCompleteProfileAndSettingsMutation(ClashDataPackageScope scope)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier admission = new();
        ProfileCatalogMutationCoordinator mutations = new(admission, new FairAsyncMutationGate());
        SettingsExportCoordinator exports = new(admission);
        FakeClashDataPackageSettings settings = new() { ActiveProfileId = "previous", MixedPort = 10000 };
        ClashDataPackageService service = new(settings, directory.Path);
        string destination = Path.Combine(directory.Path, "backup.xml");
        string catalogPath = Path.Combine(directory.Path, "ProfileCatalog.json");
        string profilePath = Path.Combine(directory.Path, "mihomo", "profiles", "new-profile", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        await File.WriteAllTextAsync(catalogPath, "previous catalog");
        await File.WriteAllTextAsync(profilePath, "previous configuration");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishMutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> mutation = mutations.ExecuteAsync(Guid.NewGuid(), async (_, _) =>
        {
            entered.SetResult();
            await finishMutation.Task;
            settings.ActiveProfileId = "new-profile";
            settings.MixedPort = 12001;
            await File.WriteAllTextAsync(catalogPath, "complete new catalog", CancellationToken.None);
            await File.WriteAllTextAsync(profilePath, "complete new configuration", CancellationToken.None);
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task export = exports.ExecuteAsync(token => service.ExportAsync(destination, scope, token), CancellationToken.None);
        try
        {
            Assert.False(export.IsCompleted);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            finishMutation.TrySetResult();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            await export.WaitAsync(TimeSpan.FromSeconds(10));
        }

        XElement root = AssertRoot(XDocument.Load(destination), scope);
        Assert.Equal("new-profile", SettingValue(root, nameof(IClashDataPackageSettings.ActiveProfileId)));
        Assert.Equal("12001", SettingValue(root, nameof(IClashDataPackageSettings.MixedPort)));
        if (scope == ClashDataPackageScope.SettingsAndProxyConfiguration)
        {
            Dictionary<string, string> files = root.Element("Files")!.Elements("File").ToDictionary(
                element => (string)element.Attribute("Path")!,
                element => Encoding.UTF8.GetString(Convert.FromBase64String(element.Value)));
            Assert.Equal("complete new catalog", files["ProfileCatalog.json"]);
            Assert.Equal("complete new configuration", files["mihomo/profiles/new-profile/config.yaml"]);
        }
        Assert.Equal(MutationAdmissionState.Open, admission.State);
    }

    [Fact]
    public async Task CoordinatedExport_WhenWaitingIsCanceled_DoesNotTouchDestination()
    {
        using TemporaryDirectory directory = new();
        string destination = Path.Combine(directory.Path, "backup.xml");
        await File.WriteAllTextAsync(destination, "previous backup");
        MutationAdmissionBarrier admission = new();
        using MutationAdmissionLease writer = admission.AcquireOrdinary();
        using CancellationTokenSource cancellation = new();
        bool invoked = false;
        Task export = new SettingsExportCoordinator(admission).ExecuteAsync(token =>
        {
            invoked = true;
            return File.WriteAllTextAsync(destination, "replacement", token);
        }, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);

        Assert.False(invoked);
        Assert.Equal("previous backup", await File.ReadAllTextAsync(destination));
        admission.EnsureActiveLease(writer);
        Assert.Equal(MutationAdmissionState.Open, admission.State);
    }

    [Fact]
    public async Task CoordinatedExport_RetainsAdmissionUntilRunningWriterFinishesAfterCancellation()
    {
        MutationAdmissionBarrier admission = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task export = new SettingsExportCoordinator(admission).ExecuteAsync(async _ =>
        {
            entered.SetResult();
            await finishExport.Task;
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            cancellation.Cancel();
            Assert.False(export.IsCompleted);
            Assert.Throws<MutationAdmissionRejectedException>(() => admission.AcquireOrdinary());
        }
        finally
        {
            finishExport.TrySetResult();
            await export.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using MutationAdmissionLease resumed = admission.AcquireOrdinary();
        admission.EnsureActiveLease(resumed);
    }

    [Fact]
    public async Task CoordinatedExport_WhenWriterFails_ReleasesAdmissionForRetry()
    {
        MutationAdmissionBarrier admission = new();
        SettingsExportCoordinator exports = new(admission);
        IOException failure = new("snapshot failed");

        IOException actual = await Assert.ThrowsAsync<IOException>(() => exports.ExecuteAsync(
            _ => Task.FromException(failure), CancellationToken.None));

        Assert.Same(failure, actual);
        bool retried = false;
        await exports.ExecuteAsync(_ => { retried = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(retried);
        Assert.Equal(MutationAdmissionState.Open, admission.State);
    }

    [Fact]
    public async Task CoordinatedExport_WhenShutdownCommitted_DoesNotStartWriter()
    {
        MutationAdmissionBarrier admission = new();
        using (MutationAdmissionLease shutdown = await admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, CancellationToken.None))
        {
            shutdown.CommitShutdown();
        }
        bool invoked = false;

        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => new SettingsExportCoordinator(admission)
            .ExecuteAsync(_ => { invoked = true; return Task.CompletedTask; }, CancellationToken.None));

        Assert.False(invoked);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, admission.State);
    }
}
