using System.Reflection;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests mainland China UI-only display replacement behavior.</summary>
public sealed class MainlandChinaTextDisplayServiceTests
{
    /// <summary>Verifies display filtering can be tested without mutating global application settings.</summary>
    [Fact]
    public void Apply_InjectedDependencies_UsesInjectedPolicy()
    {
        ConstructorInfo? constructor = typeof(MainlandChinaTextDisplayService).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(Func<MainlandChinaFeatureMode>),
                typeof(Func<bool>),
            ],
            modifiers: null);

        Assert.NotNull(constructor);

        MainlandChinaTextDisplayService service = Assert.IsType<MainlandChinaTextDisplayService>(constructor.Invoke(
        [
            () => MainlandChinaFeatureMode.FlagReplacementAndTextCompletion,
            () => false,
        ]));

        string displayText = service.Apply("香港");

        Assert.Equal("中国香港", displayText);
    }

    /// <summary>Verifies blacklisted sensitive URLs are masked only in the all-inclusive mode.</summary>
    [Fact]
    public void Apply_AllIncludingUrlBlacklist_MasksSensitiveUrls()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementOnly;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("https://pincong.rocks/topic/1");

            Assert.Equal("***", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    /// <summary>Verifies keyword filtering does not also hide blacklisted URLs.</summary>
    [Fact]
    public void Apply_KeywordFilterOnly_DoesNotMaskSensitiveUrls()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = false;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("https://pincong.rocks/topic/1");

            Assert.Equal("https://pincong.rocks/topic/1", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    /// <summary>Verifies keyword filtering covers sensitive website names in mainland China mode.</summary>
    [Fact]
    public void Apply_KeywordFilter_MasksSensitiveWebsiteNames()
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;

            string displayText = MainlandChinaTextDisplayService.Instance.Apply("品葱 大纪元");

            Assert.Equal("*** ***", displayText);
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    private const int Seed = 20261109;

    private static IReadOnlyList<string> SensitiveTerms() =>
        (string[])typeof(MainlandChinaTextDisplayService)
            .GetField("SensitiveTerms", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// Text that is not made of region names or sensitive terms: ASCII (which can never spell a sensitive Chinese
    /// term), digits in every position, network notation, and CJK characters outside every filter list.
    /// </summary>
    private static string RandomOrdinaryText(Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .:/-_,;()[]{}#@%+=一二三五七八九十人大小上下";
        int length = random.Next(0, 61);
        char[] chars = new char[length];
        for (int index = 0; index < length; index++)
        {
            chars[index] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    private static T WithFeatureMode<T>(MainlandChinaFeatureMode mode, Func<T> action)
    {
        MainlandChinaFeatureMode originalMode = AppSettingsService.Instance.MainlandChinaFeatureMode;
        bool originalUrlBlocking = AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
        try
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = mode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = false;
            return action();
        }
        finally
        {
            AppSettingsService.Instance.MainlandChinaFeatureMode = originalMode;
            AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = originalUrlBlocking;
        }
    }

    /// <summary>
    /// The filter changes only what it names. Text without a sensitive term or a region name is shown unchanged in
    /// every mode, whatever digits or network notation it holds: the filter is applied to node names, hosts, rules,
    /// addresses and statistics across the interface, so altering such text corrupts real data.
    /// </summary>
    [Theory]
    [InlineData(MainlandChinaFeatureMode.Disabled)]
    [InlineData(MainlandChinaFeatureMode.FlagReplacementOnly)]
    [InlineData(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)]
    [InlineData(MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter)]
    public void Apply_LeavesTextWithoutSensitiveTermsUnchanged(MainlandChinaFeatureMode mode)
    {
        Random random = new(Seed);
        List<string> cases = ["windows-x64", "10.0.64.2", "10.64.0.0/16", "cdn64.example.com:10064", "64 KB", "8964", "x64/arm64", string.Empty];
        for (int index = 0; index < 600; index++)
        {
            cases.Add(RandomOrdinaryText(random));
        }

        WithFeatureMode(mode, () =>
        {
            foreach (string text in cases)
            {
                Assert.Equal(text, MainlandChinaTextDisplayService.Instance.Apply(text));
            }

            return 0;
        });
    }

    /// <summary>A term is masked wherever it sits in surrounding text, and only the term is masked.</summary>
    [Fact]
    public void Apply_KeywordFilter_MasksExactlyEachTermInAnyContext()
    {
        Random random = new(Seed);
        IReadOnlyList<string> terms = SensitiveTerms();
        Assert.NotEmpty(terms);

        WithFeatureMode(MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter, () =>
        {
            foreach (string term in terms)
            {
                for (int index = 0; index < 40; index++)
                {
                    string prefix = RandomOrdinaryText(random);
                    string suffix = RandomOrdinaryText(random);
                    Assert.Equal(prefix + "***" + suffix, MainlandChinaTextDisplayService.Instance.Apply(prefix + term + suffix));
                }
            }

            return 0;
        });
    }

    /// <summary>
    /// Applying the display filter twice equals applying it once, for any text including terms and region names,
    /// because a value can pass through several display layers.
    /// </summary>
    [Theory]
    [InlineData(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion)]
    [InlineData(MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter)]
    public void Apply_IsIdempotent(MainlandChinaFeatureMode mode)
    {
        Random random = new(Seed);
        string[] fragments = [.. SensitiveTerms(), "香港", "中国香港", "澳門", "中國澳門", "澳门", "台灣", "中國台灣", "台湾", "中国台湾", "abc", "64", " ", "/"];
        WithFeatureMode(mode, () =>
        {
            for (int index = 0; index < 500; index++)
            {
                string text = string.Concat(Enumerable.Range(0, random.Next(0, 8)).Select(_ => fragments[random.Next(fragments.Length)]));
                string once = MainlandChinaTextDisplayService.Instance.Apply(text);
                Assert.Equal(once, MainlandChinaTextDisplayService.Instance.Apply(once));
            }

            return 0;
        });
    }

    /// <summary>Verifies the UI can display the unfriendly-site block list without duplicating private arrays.</summary>
    [Fact]
    public void GetUnfriendlyDisplayList_IncludesKnownTermsAndUrls()
    {
        IReadOnlyList<string> entries = MainlandChinaTextDisplayService.GetUnfriendlyDisplayList();

        Assert.All(entries, entry => Assert.StartsWith("^https?://", entry, StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("pincong", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, entry => entry.Contains("epochtimes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, entry => entry.Contains("rfa", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, entry => entry.Contains("voachinese", StringComparison.OrdinalIgnoreCase));
        Assert.True(entries.Count >= 16);
    }
}
