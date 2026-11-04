namespace ClashSharp.Tests.Unit.Model;

/// <summary>Verifies the build stamps shown on About are parsed defensively.</summary>
public sealed class ApplicationVersionTests
{
    [Theory]
    [InlineData("1.0.0+abf589fd50eda5b3382413833db23b999d26da2e", "abf589f")]
    [InlineData("1.0.0+ABCDEF0123", "abcdef0")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+local", null)]
    [InlineData("1.0.0+abc12", null)]
    [InlineData(null, null)]
    public void SourceRevisionUsesTheFirstSevenHexadecimalDigits(string? informationalVersion, string? expected)
    {
        Assert.Equal(expected, ApplicationVersion.ParseSourceRevision(informationalVersion));
    }

    [Fact]
    public void BuildTimestampIsReadAsUtc()
    {
        DateTimeOffset? timestamp = ApplicationVersion.ParseBuildTimestamp("2026-11-04T03:05:06Z");

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 3, 5, 6, TimeSpan.Zero), timestamp);
        Assert.Null(ApplicationVersion.ParseBuildTimestamp("not a time"));
        Assert.Null(ApplicationVersion.ParseBuildTimestamp(null));
    }

    [Fact]
    public void ThisBuildCarriesARevisionAndATimestamp()
    {
        Assert.NotNull(ApplicationVersion.BuildTimestamp);
        Assert.InRange(ApplicationVersion.BuildTimestamp!.Value, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
