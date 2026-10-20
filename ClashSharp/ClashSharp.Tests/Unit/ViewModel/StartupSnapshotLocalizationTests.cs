using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class StartupSnapshotLocalizationTests
{
    [Fact]
    public void DisplayText_CopiesFormatArgumentsAndRejectsMutableArguments()
    {
        object[] arguments = [12345, "adapter-a"];
        LocalizedDisplayText text = new("format", arguments);
        arguments[0] = 54321;
        arguments[1] = "adapter-b";

        Assert.Equal("12345: adapter-a", text.Resolve(_ => "{0}: {1}"));
        Assert.Equal(new LocalizedDisplayText("format", 12345, "adapter-a"), text);
        Assert.Throws<ArgumentException>(() => new LocalizedDisplayText("format", new List<string>()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CheckRow_RelocalizesTaggedTextAndKeepsObservedState(bool optional, bool unavailable)
    {
        StartupCheckItem item = new(false, "first title", "first description")
        {
            Kind = StartupCheckKind.Subscription,
            IsOptional = optional,
            IsUnavailable = unavailable,
            TitleSource = new("title"),
            DescriptionSource = new("description"),
        };
        string language = "first";
        StartupCheckDisplay row = new(item, key => language + ":" + key, allowNavigation: true);
        string glyph = row.Glyph;
        language = "second";

        row.RefreshLocalization(key => language + ":" + key);

        Assert.Equal("second:title", row.Title);
        Assert.Equal("second:description", row.Description);
        Assert.Equal(item, row.Model);
        Assert.Equal(glyph, row.Glyph);
        Assert.Equal("Links", row.NavigationTag);
        Assert.Equal($"second:StartupPrompt.State.{item.State}", row.StatusText);
        Assert.StartsWith("second:title:", row.ActionAutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckRow_WithoutProvenance_PreservesOpaqueProviderText()
    {
        StartupCheckDisplay row = new(new(false, "provider title", "provider description"), _ => "translated", allowNavigation: false);
        row.RefreshLocalization(_ => "new translation");
        Assert.Equal("provider title", row.Title);
        Assert.Equal("provider description", row.Description);
        Assert.False(row.HasNavigation);
        Assert.Null(row.NavigationTag);
    }

    [Fact]
    public void ServiceRow_UsesOnlyExplicitStatusMessageProvenance()
    {
        MihomoServiceStatus observed = new(true, false, "captured status")
        { MessageResourceKey = "service.status", IsScmRunning = true, ServiceSessionId = Guid.NewGuid() };
        StartupCheckItem item = StartupCheckService.BuildTransparentProxyCheck(true, observed, "title", "disabled", "missing", "unknown");
        StartupCheckDisplay row = new(item, key => "first:" + key, true);
        row.RefreshLocalization(key => "second:" + key);
        Assert.Equal("second:service.status", row.Description);
        Assert.Equal(item, row.Model);
        Assert.Equal(StartupCheckState.Healthy, row.Model.State);

        StartupCheckItem opaque = StartupCheckService.BuildTransparentProxyCheck(true,
            observed with { MessageResourceKey = null }, "title", "disabled", "missing", "unknown");
        Assert.Equal("captured status", new StartupCheckDisplay(opaque, _ => "translation", true).Description);
    }

    [Fact]
    public void ConflictTranslation_RetainsCapturedArgumentsRepairTargetAndRunningState()
    {
        int repairs = 0;
        StartupConflictIssue issue = new(StartupConflictKind.MixedPortOccupied, "old title", "old description", "old repair", _ =>
        { repairs++; return Task.FromResult(new StartupConflictRepairResult(true, "done")); })
        {
            TitleSource = new("title"),
            DescriptionSource = new("description", 12345),
            RepairTextSource = new("repair"),
        };
        string language = "first";
        string GetText(string key) => key == "description" ? language + ": port {0}" : language + ":" + key;
        StartupConflictDisplay row = new(issue, GetText);
        Assert.True(row.BeginRepair());
        language = "second";

        row.RefreshLocalization();

        Assert.Same(issue, row.Issue);
        Assert.Equal("second: port 12345", row.Description);
        Assert.Equal("second:StartupConflict.Status.Fixing", row.StatusText);
        Assert.True(row.IsRepairRunning);
        Assert.False(row.CanRepair);
        Assert.Equal(0, repairs);
        row.CompleteRepair(new(true, "original success") { MessageSource = new("completed") });
        language = "third";
        row.RefreshLocalization();
        Assert.Equal("third:completed", row.StatusText);
        Assert.False(row.CanRepair);
        Assert.False(row.BeginRepair());
        Assert.Equal(0, repairs);
    }

    [Fact]
    public void ConflictTranslation_PreservesOpaqueRepairMessageAndAllowsExplicitRetryAfterFailure()
    {
        StartupConflictIssue issue = new(StartupConflictKind.MixedPortOccupied, "title", "description", "repair",
            _ => throw new InvalidOperationException("Translation must never call repair."));
        StartupConflictDisplay row = new(issue, key => "translated:" + key);
        Assert.True(row.BeginRepair());
        row.CompleteRepair(new(false, "provider message"));
        row.RefreshLocalization();
        Assert.Equal("provider message", row.StatusText);
        Assert.True(row.CanRepair);
        Assert.True(row.BeginRepair());
        Assert.Equal("translated:StartupConflict.Status.Fixing", row.StatusText);
    }
}
