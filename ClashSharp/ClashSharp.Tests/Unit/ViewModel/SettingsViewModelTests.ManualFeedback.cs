using System.Diagnostics;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData("login.microsoftonline.com", "Microsoft")]
    [InlineData("www.bing.com", "Bing")]
    [InlineData("cloudflare-dns.com", "Cloudflare")]
    [InlineData("registry.npmjs.org", "npm")]
    [InlineData("files.pythonhosted.org", "PyPI")]
    [InlineData("api.nuget.org", "NuGet")]
    [InlineData("hub.docker.com", "Docker")]
    [InlineData("cdn.steamcommunity.com", "Steam")]
    [InlineData("claude.ai", "Anthropic")]
    [InlineData("huggingface.co", "Hugging Face")]
    [InlineData("weixin.qq.com", "微信")]
    [InlineData("WWW.APPLE.COM.", "Apple")]
    [InlineData("microsoft.com.attacker.invalid", null)]
    [InlineData("evilmicrosoft.com", null)]
    [InlineData("npmjs.org.attacker.invalid", null)]
    public void PublicSiteNames_UseSpecificDnsBoundaries(string host, string? expected)
    {
        Assert.Equal(expected, ConnectionTestSiteCatalog.Find(host));
    }

    [Fact]
    public async Task ConnectionProbeSynchronousPreamble_DoesNotBlockTheCaller_AndRejectsReentry()
    {
        using ManualResetEventSlim release = new(false);
        SettingsViewModel model = CreateConnectionTestViewModel((_, token) =>
        {
            release.Wait(TimeSpan.FromSeconds(2), token);
            return Task.FromResult(204);
        });
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<ConnectionTestReport> active = model.RunConnectionTestAsync(CancellationToken.None);
        try
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "A synchronous network preamble blocked the caller.");
            Assert.True(model.IsConnectionTestRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.RunConnectionTestAsync(CancellationToken.None));
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(model.IsConnectionTestRunning);
    }

    [Fact]
    public void UnitResetScope_IsIndependentFromLanguageAndAppearance()
    {
        string[] units = SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.Units)
            .Select(definition => definition.Key.Value).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.Equal<string>(["ClockFormat", "DataUnitStyle", "RateUnit"], units);
        Assert.DoesNotContain(SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.Basic),
            definition => units.Contains(definition.Key.Value, StringComparer.Ordinal));
    }
}
