/*
 * Master Hero Status Layout Service Tests
 * Verifies persisted hero-card status slots normalize to a compact, predictable layout
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MasterHeroStatusLayoutServiceTests.cs
 * @date: 2026-06-29
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MasterHeroStatusLayoutServiceTests
{
    [Fact]
    public void GetLayout_WhenUnset_ReturnsEightDefaultSlots()
    {
        FakeHeroLayoutSettings settings = new();
        MasterHeroStatusLayoutService service = new(settings);

        IReadOnlyList<MasterHeroStatusItemKind> layout = service.GetLayout();

        Assert.Equal(
            [
                MasterHeroStatusItemKind.CoreStatus,
                MasterHeroStatusItemKind.SystemProxy,
                MasterHeroStatusItemKind.TransparentProxy,
                MasterHeroStatusItemKind.CurrentNode,
                MasterHeroStatusItemKind.UploadRate,
                MasterHeroStatusItemKind.DownloadRate,
                MasterHeroStatusItemKind.TotalTraffic,
                MasterHeroStatusItemKind.Availability,
            ],
            layout);
        Assert.Equal(8, layout.Count);
    }

    [Fact]
    public void GetCandidates_ExposesFourteenCommonStatusItems()
    {
        MasterHeroStatusLayoutService service = new(new FakeHeroLayoutSettings());

        IReadOnlyList<MasterHeroStatusItemKind> candidates = service.GetCandidates();

        Assert.Equal(14, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
        Assert.Contains(MasterHeroStatusItemKind.MihomoService, candidates);
        Assert.Contains(MasterHeroStatusItemKind.ActiveConnections, candidates);
    }

    [Fact]
    public void SaveLayout_WhenLayoutContainsDuplicatesAndInvalidNames_NormalizesToEightUniqueKnownSlots()
    {
        FakeHeroLayoutSettings settings = new();
        MasterHeroStatusLayoutService service = new(settings);

        IReadOnlyList<MasterHeroStatusItemKind> layout = service.SaveSerializedLayout(
            "UploadRate,UploadRate,Unknown,DownloadRate,CoreStatus");

        Assert.Equal(8, layout.Count);
        Assert.Equal(layout.Count, layout.Distinct().Count());
        Assert.Equal(MasterHeroStatusItemKind.UploadRate, layout[0]);
        Assert.Equal(MasterHeroStatusItemKind.DownloadRate, layout[1]);
        Assert.Equal("UploadRate,DownloadRate,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,TotalTraffic,Availability", settings.MasterHeroStatusLayout);
    }

    [Fact]
    public void ResetLayout_RestoresDefaultAndPersistsIt()
    {
        FakeHeroLayoutSettings settings = new() { MasterHeroStatusLayout = "UploadRate,DownloadRate,Latency" };
        MasterHeroStatusLayoutService service = new(settings);

        IReadOnlyList<MasterHeroStatusItemKind> layout = service.ResetLayout();

        Assert.Equal(MasterHeroStatusLayoutService.DefaultLayout, layout);
        Assert.Equal(
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability",
            settings.MasterHeroStatusLayout);
    }

    private sealed class FakeHeroLayoutSettings : IMasterHeroStatusLayoutSettings
    {
        public string MasterHeroStatusLayout { get; set; } = string.Empty;
    }
}
