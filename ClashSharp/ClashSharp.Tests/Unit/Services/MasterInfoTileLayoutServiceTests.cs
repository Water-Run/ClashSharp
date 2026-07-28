using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MasterInfoTileLayoutServiceTests
{
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
                "core",
                "upload-rate",
                "download-rate",
                "active-connections",
                "transparent-proxy",
                "latency",
                "active-profile",
                "current-mode",
            ],
            layout);
    }

    [Fact]
    public void SaveLayout_AllowsAnExplicitEmptySelection()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = service.SaveLayout([], AvailableTileIds);

        Assert.Empty(layout);
        Assert.Equal(string.Empty, settings.MasterInfoTileLayout);
    }

    [Fact]
    public void SaveLayout_FiltersUnknownIdsAndPersistsCanonicalIdsInRequestedOrder()
    {
        FakeInfoTileLayoutSettings settings = new();
        MasterInfoTileLayoutService service = new(settings);

        IReadOnlyList<string> layout = service.SaveLayout(
            ["memory-usage", "core", "CORE", "unknown"],
            AvailableTileIds);

        Assert.Equal(["memory-usage", "core"], layout);
        Assert.Equal("memory-usage,core", settings.MasterInfoTileLayout);
    }

    private sealed class FakeInfoTileLayoutSettings : IMasterInfoTileLayoutSettings
    {
        public string MasterInfoTileLayout { get; set; } =
            string.Join(",", MasterInfoTileLayoutService.DefaultLayout);
    }
}
