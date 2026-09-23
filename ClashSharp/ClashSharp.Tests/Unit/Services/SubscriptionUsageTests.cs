using System.Text.Json;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.Services;

public sealed class SubscriptionUsageTests
{
    [Fact]
    public void Parse_PreservesReportedValuesAndAcceptsCaseWhitespaceAndDecimalNotation()
    {
        SubscriptionUsage? usage = SubscriptionUsage.Parse(" UPLOAD = 1.024e3 ; Download=2048; total=1073741824; expire=1893456000; other=x");
        Assert.Equal(new SubscriptionUsage(1024, 2048, 1073741824, 1893456000), usage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown=123")]
    [InlineData("upload=-1; download=NaN; total=9223372036854775808; expire=253402300800")]
    [InlineData("upload=Infinity; expire=-1")]
    public void Parse_AbsentAndInvalidValuesRemainUnknown(string? header)
    {
        Assert.Null(SubscriptionUsage.Parse(header));
    }

    [Fact]
    public void Parse_PartialZeroAndDuplicateInvalidFieldsDoNotInventUsage()
    {
        Assert.Equal(new SubscriptionUsage(null, 0, null, 0),
            SubscriptionUsage.Parse("upload=42; upload=bad; download=0; expire=0"));
        Assert.Null(SubscriptionUsage.Parse(new string('x', 8193)));
    }

    [Fact]
    public void Display_FormatsKnownAndUnknownMetadataWithoutOverflow()
    {
        static string Localize(string key) => key switch
        {
            "Links.Usage.Format" => "Usage: {0} / {1}",
            "Links.Expiry.Format" => "Expires: {0}",
            "Links.Metadata.NotProvided" => "unknown",
            "Links.Metadata.NoExpiry" => "none",
            _ => key,
        };
        ProfileSubscriptionLink link = new("id", "test", "https://example.com", true, 24, default, "OK",
            Usage: new SubscriptionUsage(1024, 2048, 1073741824, 0));
        ModelDisplayMapper mapper = new(static text => text);
        ProfileSubscriptionLinkDisplay display = mapper.Map(link).WithDetails(Localize);
        Assert.Equal("Usage: 3 KB / 1 GB", display.UsageDisplay);
        Assert.Equal("Expires: none", display.ExpiryDisplay);
        Assert.Equal("Usage: unknown / unknown", mapper.Map(link with { Usage = null }).WithDetails(Localize).UsageDisplay);
        Assert.Contains("EB", mapper.Map(link with { Usage = new(long.MaxValue, long.MaxValue, null, null) })
            .WithDetails(Localize).UsageDisplay, StringComparison.Ordinal);
        Assert.Contains("Usage: unknown", mapper.Map(link with { Usage = new(1024, null, null, null) })
            .WithDetails(Localize).UsageDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyCatalog_DeserializesWithoutMetadata()
    {
        ProfileSubscriptionLink link = JsonSerializer.Deserialize<ProfileSubscriptionLink>(
            "{\"Id\":\"legacy\",\"Name\":\"Legacy\",\"Uri\":\"https://example.com\",\"IsEnabled\":true,\"UpdateIntervalHours\":24,\"Status\":\"OK\"}");
        Assert.Null(link.Usage);
        Assert.Equal(0, link.Revision);
    }
}
