using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MasterInfoTileLayoutServiceTests
{
    private const string PreviousDefaultPresets = "@daily;@traffic;@subscriptions;@diagnostics;@minimal";

    private static readonly IReadOnlyList<string> AvailableTileIds =
    [
        "core",
        "upload-rate",
        "download-rate",
        "active-connections",
        "transparent-proxy",
        "latency",
        "active-profile",
        "current-mode",
        "memory-usage",
    ];

    [Fact]
    public void GetLayout_PreservesPersistedOrderAndIgnoresUnknownOrDuplicateIds()
    {
        FakeInfoTileLayoutSettings settings = new()
        {
            MasterInfoTileLayout = "latency,unknown,core,LATENCY,memory-usage",
        };
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = service.GetLayout(AvailableTileIds);

        Assert.Equal(["latency", "core", "memory-usage"], layout);
    }

    [Fact]
    public void GetLayout_WhenAllPersistedIdsAreUnknown_UsesKnownDefaults()
    {
        FakeInfoTileLayoutSettings settings = new()
        {
            MasterInfoTileLayout = "removed-tile",
        };
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = service.GetLayout(AvailableTileIds);

        Assert.Equal(
            [
                "upload-rate",
                "download-rate",
                "latency",
                "active-connections",
                "active-profile",
                "transparent-proxy",
            ],
            layout);
    }

    [Fact]
    public async Task SaveLayout_AllowsAnExplicitEmptySelection()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = await service.SaveLayoutAsync([], AvailableTileIds, CancellationToken.None);

        Assert.Empty(layout);
        Assert.Equal(string.Empty, settings.MasterInfoTileLayout);
    }

    [Fact]
    public async Task SaveLayout_FiltersUnknownIdsAndPersistsCanonicalIdsInRequestedOrder()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = await service.SaveLayoutAsync(
            ["memory-usage", "core", "CORE", "unknown"],
            AvailableTileIds, CancellationToken.None);

        Assert.Equal(["memory-usage", "core"], layout);
        Assert.Equal("memory-usage,core", settings.MasterInfoTileLayout);
    }

    [Fact]
    public void GetPresets_WhenStoredListIsUnreadable_UsesTheBuiltInList()
    {
        FakeInfoTileLayoutSettings settings = new() { MasterInfoTilePresets = "not-a-template" };
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<MasterInfoTilePresetEntry> presets = service.GetPresets();

        Assert.Equal(MasterInfoTilePresetList.BuiltInIds, presets.Select(static entry => entry.BuiltInId));
    }

    [Fact]
    public void GetPresets_DisplaysNewDefaultsWithoutRewritingThePreviousDefaultValue()
    {
        FakeInfoTileLayoutSettings settings = new() { MasterInfoTilePresets = PreviousDefaultPresets };
        MasterInfoTileLayoutService service = new(settings);

        Assert.Equal(MasterInfoTilePresetList.BuiltInIds, service.GetPresets().Select(static entry => entry.BuiltInId));
        Assert.Equal(PreviousDefaultPresets, settings.MasterInfoTilePresets);
    }

    [Theory]
    [InlineData(PreviousDefaultPresets + ";custom:Work:core")]
    [InlineData("@minimal;@diagnostics;@subscriptions;@traffic;@daily")]
    [InlineData("")]
    public void GetPresets_PreservesCustomizedLists(string saved)
    {
        FakeInfoTileLayoutSettings settings = new() { MasterInfoTilePresets = saved };
        MasterInfoTileLayoutService service = new(settings);

        Assert.Equal(saved, MasterInfoTilePresetList.Format(service.GetPresets()));
        Assert.Equal(saved, settings.MasterInfoTilePresets);
    }

    [Fact]
    public async Task SavePresets_PersistsCanonicalOrderAndRestoreRemovesCustomTemplates()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<MasterInfoTilePresetEntry> saved = await service.SavePresetsAsync(
            [MasterInfoTilePresetEntry.Custom(" Work ", ["Latency", "core"]), MasterInfoTilePresetEntry.BuiltIn("minimal")],
            CancellationToken.None);

        Assert.Equal("custom:Work:latency,core;@minimal", settings.MasterInfoTilePresets);
        Assert.Equal(new string?[] { "Work", null }, saved.Select(static entry => entry.Name));
        Assert.Equal(saved.Select(static entry => entry.Name), service.GetPresets().Select(static entry => entry.Name));

        IReadOnlyList<MasterInfoTilePresetEntry> restored = await service.RestoreDefaultPresetsAsync(CancellationToken.None);

        Assert.Equal(MasterInfoTilePresetList.DefaultCanonicalText, settings.MasterInfoTilePresets);
        Assert.All(restored, static entry => Assert.True(entry.IsBuiltIn));
    }

    [Fact]
    public async Task SavePresets_RejectsListsBeyondTheLimitWithoutWriting()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);
        MasterInfoTilePresetEntry[] tooMany = Enumerable.Range(0, MasterInfoTilePresetList.MaximumEntries + 1)
            .Select(static index => MasterInfoTilePresetEntry.Custom($"T{index}", ["core"]))
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SavePresetsAsync(tooMany, CancellationToken.None));

        Assert.Equal(MasterInfoTilePresetList.DefaultCanonicalText, settings.MasterInfoTilePresets);
    }

    private sealed class FakeInfoTileLayoutSettings : IMasterInfoTileLayoutSettings
    {
        public string MasterInfoTileLayout { get; set; } =
            string.Join(",", MasterInfoTileLayoutService.DefaultLayout);

        public string MasterInfoTilePresets { get; set; } = MasterInfoTilePresetList.DefaultCanonicalText;

        public Task SaveInfoTileLayoutAsync(string layout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MasterInfoTileLayout = layout;
            return Task.CompletedTask;
        }

        public Task SaveInfoTilePresetsAsync(string presets, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MasterInfoTilePresets = presets;
            return Task.CompletedTask;
        }
    }
}
