extern alias ClashSharpUi;

using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>
/// WinUI names a list row after the item's string form. Without an override a screen reader announces the
/// CLR type name (for example "ClashSharp.ViewModel.ProxyNodeDisplay") for every row.
/// </summary>
public sealed class ListItemAutomationNameTests
{
    private static readonly RegionMetadata Region = new("CN", "China", "cn");

    [Fact]
    public void ProxyNodeRow_IsNamedAfterTheNodeProtocolAndLatency()
    {
        ProxyNodeDisplay row = new(new ProxyNode("Node A", "vless", Region, 42), "Node A") { LatencyDisplay = "42 ms" };

        Assert.Equal("Node A, vless, 42 ms", row.ToString());
    }

    [Fact]
    public void RuleRow_IsNamedAfterTheTypePayloadAndAction()
    {
        RulePreviewDisplay row = new(new RulePreview("src", "DOMAIN-SUFFIX", "example.com", "PROXY", 3), "src", "example.com");

        Assert.Equal("DOMAIN-SUFFIX, example.com, PROXY", row.ToString());
    }

    [Fact]
    public void LogRow_IsNamedAfterTimeLevelSourceAndMessage()
    {
        LogRecord record = new(new DateTimeOffset(2026, 11, 9, 8, 30, 0, TimeSpan.Zero), "Error", "Core", "Core stopped.", string.Empty);
        LogRecordDisplay row = new(record, "Error", "Core");

        string name = row.ToString();

        Assert.EndsWith("Error, Core, Core stopped.", name, StringComparison.Ordinal);
        Assert.DoesNotContain("ClashSharp.ViewModel", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Core status", "Running", "Core status: Running")]
    [InlineData("Core status", "", "Core status")]
    public void HeroStatusRow_IsNamedAfterTitleAndValue(string title, string value, string expected)
    {
        MasterHeroStatusItemViewModel row = new(MasterHeroStatusItemKind.CoreStatus, title, value);

        Assert.Equal(expected, row.ToString());
    }

    [Fact]
    public void EveryRowTypeOverridesToStringSoNoRowIsAnnouncedByItsTypeName()
    {
        Type[] rowTypes =
        [
            typeof(ProfileSubscriptionLinkDisplay), typeof(ConfigurationProfileDisplay), typeof(ProxyNodeDisplay),
            typeof(RulePreviewDisplay), typeof(LogRecordDisplay), typeof(MasterHeroStatusItemViewModel),
            typeof(ActiveConnectionDisplayRow), typeof(ClashSharpUi::ClashSharp.ViewModel.TriggerTaskItemViewModel),
            typeof(ClashSharpUi::ClashSharp.ViewModel.TriggerConditionEditorViewModel),
            typeof(ClashSharpUi::ClashSharp.ViewModel.TriggerActionEditorViewModel),
        ];

        foreach (Type type in rowTypes)
        {
            System.Reflection.MethodInfo? toString = type.GetMethod(nameof(ToString), Type.EmptyTypes);
            Assert.True(toString is not null && toString.DeclaringType == type, type.Name + " must override ToString.");
        }
    }
}
