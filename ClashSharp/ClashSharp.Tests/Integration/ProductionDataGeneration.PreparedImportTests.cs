extern alias ClashSharpUi;
using System.Xml.Linq;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task PreparedImport_UsesConfirmedSettingsEvenWhenSelectedPathChanges(string change)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        var settings = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        string input = await WritePreparedImportInputAsync(directory, 34567);
        var replacement = CreatePreparedReplacement(fixture);
        int nativeWrites = 0;
        network.BeforeApply = (_, _) => nativeWrites++;

        using IPreparedSettingsImport preview = await replacement.PrepareImportAsync(input, CancellationToken.None);

        Assert.Equal(ClashDataPackageScope.Settings, preview.Scope);
        Assert.Equal(before, fixture.Manager.CurrentManifest);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Equal(0, nativeWrites);
        if (change == "delete") { File.Delete(input); }
        else if (change == "rename") { File.Move(input, input + ".moved"); }
        else
        {
            XDocument other = EmptyPackage();
            other.Root!.SetAttributeValue("Scope", "SettingsAndProxyConfiguration");
            other.Root.Element("Settings")!.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", "45678")));
            await File.WriteAllTextAsync(input, other.ToString());
        }

        var result = await replacement.ImportAsync(preview, CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Equal(ClashDataPackageScope.Settings, preview.Scope);
        Assert.Equal(34567, settings.MixedPort);
        Assert.NotEqual(before.Descriptor.GenerationId, fixture.Manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task PreparedImport_DetachesValidatedProfileBytesBeforeConfirmation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using DataGenerationTestDirectory backups = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var profile = await fixture.Containers[^1].Profiles.ImportLocalProfileAsync(await WriteProfileInputAsync(directory), CancellationToken.None);
        string relative = $"mihomo/profiles/{profile.ProfileId}/config.yaml";
        byte[] expected = await File.ReadAllBytesAsync(Path.Combine(fixture.Manager.CurrentManifest.Descriptor.RootPath, relative));
        string input = Path.Combine(backups.RootPath, "profile-preview.xml");
        await new SettingsExportCoordinator(fixture.Admission).ExecuteAsync((lease, token) =>
            new UiData.GenerationDataPackageExporter(fixture.Manager, fixture.Admission, directory.RootPath)
                .ExportAdmittedAsync(input, ClashDataPackageScope.SettingsAndProxyConfiguration, lease, token), CancellationToken.None);
        var replacement = CreatePreparedReplacement(fixture);
        using IPreparedSettingsImport preview = await replacement.PrepareImportAsync(input, CancellationToken.None);
        Assert.Equal(ClashDataPackageScope.SettingsAndProxyConfiguration, preview.Scope);
        await File.WriteAllTextAsync(input, "This is no longer an XML package.");

        await replacement.ImportAsync(preview, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(fixture.Manager.CurrentManifest.Descriptor.RootPath, relative)));
    }

    [Fact]
    public async Task PreparedImport_DisposalAbandonsPreviewWithoutQuiescingOrChangingData()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        var settings = await StartReplacementFixtureAsync(fixture, network);
        var before = fixture.Manager.CurrentManifest;
        int port = settings.MixedPort;
        int nativeWrites = 0;
        network.BeforeApply = (_, _) => nativeWrites++;
        var replacement = CreatePreparedReplacement(fixture);
        IPreparedSettingsImport preview = await replacement.PrepareImportAsync(await WritePreparedImportInputAsync(directory, 34567), CancellationToken.None);

        preview.Dispose();
        preview.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.ImportAsync(preview, CancellationToken.None));
        Assert.Equal(before, fixture.Manager.CurrentManifest);
        Assert.Equal(port, settings.MixedPort);
        Assert.Equal(0, nativeWrites);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedImport_RejectsForeignPreviewWithoutConsumingTheOwnersPreview(bool forged)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var owner = CreatePreparedReplacement(fixture);
        var other = CreatePreparedReplacement(fixture);
        using IPreparedSettingsImport preview = await owner.PrepareImportAsync(await WritePreparedImportInputAsync(directory, 34567), CancellationToken.None);
        using IPreparedSettingsImport unknown = new ForgedSettingsImport();

        await Assert.ThrowsAsync<ArgumentException>(() => other.ImportAsync(forged ? unknown : preview, CancellationToken.None));
        await owner.ImportAsync(preview, CancellationToken.None);

        Assert.Equal(34567, settings.MixedPort);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ImportAsync(preview, CancellationToken.None));
    }

    [Fact]
    public async Task PreparedImport_CancellationBeforeConsumptionLeavesPreviewAvailableForExplicitRetry()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        var settings = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var replacement = CreatePreparedReplacement(fixture);
        using IPreparedSettingsImport preview = await replacement.PrepareImportAsync(await WritePreparedImportInputAsync(directory, 34567), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement.ImportAsync(preview, cancellation.Token));
        await replacement.ImportAsync(preview, CancellationToken.None);

        Assert.Equal(34567, settings.MixedPort);
    }

    [Fact]
    public async Task PreparedImport_DataGenerationChangeRequiresAnotherPreviewAndConfirmation()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        _ = await StartReplacementFixtureAsync(fixture, network);
        var replacement = CreatePreparedReplacement(fixture);
        using IPreparedSettingsImport preview = await replacement.PrepareImportAsync(await WritePreparedImportInputAsync(directory, 34567), CancellationToken.None);
        await replacement.ResetAllSettingsAsync(CancellationToken.None);
        var current = fixture.Manager.CurrentManifest;
        int writes = 0;
        network.BeforeApply = (_, _) => writes++;

        await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.ImportAsync(preview, CancellationToken.None));

        Assert.Equal(current, fixture.Manager.CurrentManifest);
        Assert.Equal(0, writes);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
    }

    [Fact]
    public async Task PreparedImport_DisposalDuringAnAcceptedTransactionCannotEraseItsContents()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        NetworkSurface network = new();
        var settings = await StartReplacementFixtureAsync(fixture, network);
        var replacement = CreatePreparedReplacement(fixture);
        using IPreparedSettingsImport preview = await replacement.PrepareImportAsync(await WritePreparedImportInputAsync(directory, 34567), CancellationToken.None);
        network.BeforeApply = (_, _) => preview.Dispose();

        await replacement.ImportAsync(preview, CancellationToken.None);

        Assert.Equal(34567, settings.MixedPort);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task PreparedImport_ValidatesFullContentsBeforeReturningAnImportableScope()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        _ = await StartReplacementFixtureAsync(fixture, new NetworkSurface());
        var before = fixture.Manager.CurrentManifest;
        XDocument package = EmptyPackage();
        package.Root!.Element("Settings")!.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", "not-a-port")));
        string input = Path.Combine(directory.RootPath, "invalid-preview.xml");
        await File.WriteAllTextAsync(input, package.ToString());
        var replacement = CreatePreparedReplacement(fixture);

        await Assert.ThrowsAsync<FormatException>(() => replacement.PrepareImportAsync(input, CancellationToken.None));

        Assert.Equal(before, fixture.Manager.CurrentManifest);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    private static UiData.GenerationSettingsDataReplacement CreatePreparedReplacement(Fixture fixture) =>
        new(fixture.CreateReplacementCoordinator(), () => throw new InvalidOperationException("Unexpected restart flag."),
            _ => throw new InvalidOperationException("Unexpected recovery restart."));

    private static async Task<string> WritePreparedImportInputAsync(DataGenerationTestDirectory directory, int port)
    {
        XDocument package = EmptyPackage();
        package.Root!.Element("Settings")!.Add(new XElement("Setting", new XAttribute("Name", "MixedPort"), new XAttribute("Value", port)));
        string path = Path.Combine(directory.RootPath, "prepared-import.xml");
        await File.WriteAllTextAsync(path, package.ToString());
        return path;
    }

    private sealed class ForgedSettingsImport : IPreparedSettingsImport
    {
        public ClashDataPackageScope Scope => ClashDataPackageScope.Settings;
        public void Dispose() { }
    }
}
