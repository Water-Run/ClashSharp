using System.Globalization;
using System.Net.Http;
using System.Text;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Contracts for provider-controlled response headers: a provider can influence a display name and usage
/// metadata, but never smuggle control characters, path components, unbounded text or malformed counters.
/// </summary>
public sealed class SubscriptionHeaderContractTests
{
    private const int Seed = 20261109;
    private const int Iterations = 3000;

    private static readonly string[] Atoms =
    [
        "a", "Z", "0", "9", " ", "  ", "-", "_", ".", "..", "/", "\\", ":", "\"", "'", "*", "?", "<", ">", "|",
        "\u0000", "\u0007", "\t", "\r", "\n", "\u007f", "\u0085", "\u200b", "\u202e", "\u202c", "\u00a0",
        "订阅", "節點", "Привет", "עברית", "مرحبا", "😀", "e\u0301", "\ud83d\udc68\u200d\ud83d\udc69",
        ".yaml", ".YML", ".txt", ".conf", ".yaml.yaml", "base64:", "BASE64:", "utf-8''", "%E8%AE%A2", "%ZZ", "%",
        "CON", "NUL", "C:", "..\\..\\", "../",
    ];

    private static string RandomText(Random random, int maxAtoms)
    {
        StringBuilder text = new();
        int count = random.Next(0, maxAtoms + 1);
        for (int index = 0; index < count; index++)
        {
            text.Append(Atoms[random.Next(Atoms.Length)]);
        }

        return text.ToString();
    }

    private static HttpResponseMessage Response(string? title = null, string? disposition = null, params string[] extraTitles)
    {
        HttpResponseMessage response = new() { Content = new StringContent("x") };
        if (title is not null)
        {
            response.Headers.TryAddWithoutValidation("profile-title", title);
        }

        foreach (string extra in extraTitles)
        {
            response.Headers.TryAddWithoutValidation("profile-title", extra);
        }

        if (disposition is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", disposition);
        }

        return response;
    }

    private static string? Name(string? title = null, string? disposition = null)
    {
        using HttpResponseMessage response = Response(title, disposition);
        return SubscriptionProfileName.Read(response);
    }

    private static string Base64Title(string text) => "base64:" + Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(text));

    private static string PercentEncode(string text)
    {
        StringBuilder builder = new();
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsLoneSurrogate(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ profile name

    private static void AssertSafeDisplayName(string? name, string origin)
    {
        if (name is null)
        {
            return;
        }

        Assert.True(name.Length is > 0 and <= 128, $"length {name.Length} from {origin}");
        Assert.False(string.IsNullOrWhiteSpace(name), $"blank name from {origin}");
        Assert.DoesNotContain(name, static character => char.IsControl(character));
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(name.Trim(), name);
    }

    [Fact]
    public void ProfileTitle_EveryRouteYieldsNullOrASafeBoundedDisplayName()
    {
        Random random = new(Seed);
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            string text = RandomText(random, 12);
            string origin = $"#{iteration} '{text.Replace("\u0000", "<NUL>", StringComparison.Ordinal)}'";
            AssertSafeDisplayName(Name(title: text), "plain title " + origin);
            if (!IsLoneSurrogate(text))
            {
                AssertSafeDisplayName(Name(title: Base64Title(text)), "base64 title " + origin);
                AssertSafeDisplayName(Name(disposition: "attachment; filename*=UTF-8''" + PercentEncode(text)), "filename* " + origin);
            }

            AssertSafeDisplayName(Name(disposition: "attachment; filename=\"" + text.Replace("\"", "'", StringComparison.Ordinal) + "\""), "filename " + origin);
            AssertSafeDisplayName(Name(disposition: text), "raw disposition " + origin);
        }
    }

    [Fact]
    public void ProfileTitle_PlainAndBase64FormsAreEquivalentForEveryText()
    {
        Random random = new(Seed + 1);
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            string text = RandomText(random, 10);
            if (IsLoneSurrogate(text) || text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? plain = Name(title: text);
            string? encoded = Name(title: Base64Title(text));
            Assert.True(plain == encoded, $"#{iteration}: plain '{plain}' vs base64 '{encoded}' for '{text}'");
        }
    }

    [Fact]
    public void ProfileTitle_Base64DecodesToTheExactUnicodeTitle()
    {
        foreach (string title in new[] { "订阅服务", "Plan Ω", "עברית", "مرحبا بالعالم", "😀 fast", "Straße-1", "e\u0301clair" })
        {
            Assert.Equal(title, Name(title: Base64Title(title)));
            Assert.Equal(title, Name(title: Base64Title(title).Replace("base64:", "BASE64:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void ProfileTitle_MalformedBase64NeverLeaksTheEncodedPayloadAsAName()
    {
        string[] payloads = ["not-valid", "====", "A", "AAAAA", "!@#$", "5L+h5oGv", "//79", "\u00ff\u00fe"];
        foreach (string payload in payloads)
        {
            string? name = Name(title: "base64:" + payload);
            // Either rejected, or decoded as strict UTF-8 text; never the literal 'base64:...' header.
            Assert.False(name?.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) ?? false, payload);
        }

        // 0xFF 0xFE is not UTF-8: it must be rejected rather than decoded with replacement characters.
        Assert.Null(Name(title: "base64:" + Convert.ToBase64String([0xFF, 0xFE, 0x41])));
        Assert.Null(Name(title: "base64:" + Convert.ToBase64String([0xC0, 0xAF])));
        Assert.Null(Name(title: "base64:" + Convert.ToBase64String([0xED, 0xA0, 0x80])));
    }

    [Fact]
    public void ProfileTitle_PrecedenceIsTitleThenExtendedFileNameThenFileNameThenNothing()
    {
        Assert.Equal("Title", Name("Title", "attachment; filename*=UTF-8''Star.yaml; filename=Plain.yaml"));
        Assert.Equal("Star", Name(null, "attachment; filename*=UTF-8''Star.yaml; filename=Plain.yaml"));
        Assert.Equal("Plain", Name(null, "attachment; filename=Plain.yaml"));
        Assert.Equal("Plain", Name("   ", "attachment; filename=Plain.yaml"));
        Assert.Equal("Plain", Name("bad\u0007title", "attachment; filename=Plain.yaml"));
        Assert.Equal("Plain", Name("base64:@@", "attachment; filename=Plain.yaml"));
        Assert.Null(Name());
        Assert.Null(Name("   "));
        Assert.Null(Name(null, "attachment"));
        Assert.Null(Name(null, "inline; filename="));

        using HttpResponseMessage twoTitles = Response("First", null, "Second");
        Assert.Equal("First", SubscriptionProfileName.Read(twoTitles));
    }

    [Fact]
    public void ProfileTitle_HeaderSizeLimitsAreEnforcedAtTheDocumentedBoundaries()
    {
        // The title budget is 2048 header characters; the name budget is 128 characters after trimming.
        Assert.Equal(new string('n', 128), Name(title: new string('n', 128)));
        Assert.Null(Name(title: new string('n', 129)));
        Assert.Equal("fallback", Name(title: new string('n', 2049), disposition: "attachment; filename=fallback.yaml"));
        string padded = "base64:" + Convert.ToBase64String("ok"u8.ToArray()) + new string(' ', 2100);
        Assert.Equal("fallback", Name(title: padded, disposition: "attachment; filename=fallback.yaml"));
        Assert.Null(Name(disposition: "attachment; filename=" + new string('d', 4096)));
        Assert.Equal("fallback", Name(disposition: "attachment; filename=fallback.yaml; x=" + new string('x', 20)));
        Assert.Null(Name(disposition: "attachment; filename=fallback.yaml; x=" + new string('x', 4100)));
    }

    [Fact]
    public void ProfileTitle_StripsOneKnownExtensionAndEveryPathComponent()
    {
        (string Header, string? Expected)[] cases =
        [
            ("attachment; filename=Plan.yaml", "Plan"),
            ("attachment; filename=Plan.YML", "Plan"),
            ("attachment; filename=Plan.txt", "Plan"),
            ("attachment; filename=Plan.conf", "Plan"),
            ("attachment; filename=Plan.yaml.yaml", "Plan.yaml"),
            ("attachment; filename=Plan.tar", "Plan.tar"),
            ("attachment; filename=\"dir/Plan.yaml\"", "Plan"),
            ("attachment; filename=\"dir\\sub\\Plan.yaml\"", "Plan"),
            ("attachment; filename=\"../../etc/passwd\"", "passwd"),
            ("attachment; filename=\"C:\\Windows\\win.ini\"", "win.ini"),
            ("attachment; filename=\"trailing/\"", null),
            ("attachment; filename=.yaml", null),
        ];
        foreach ((string header, string? expected) in cases)
        {
            Assert.True(expected == Name(disposition: header), header);
        }
    }

    [Fact]
    public void ProfileTitle_NeverThrowsForArbitraryHeaderBytes()
    {
        Random random = new(Seed + 2);
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            string text = RandomText(random, 20);
            _ = Name(title: text, disposition: text);
            _ = Name(title: "base64:" + text, disposition: "attachment; filename*=" + text);
            _ = Name(disposition: "attachment; filename*=UTF-8''" + text);
            _ = Name(disposition: "attachment; filename*=iso-8859-1''" + text);
            _ = Name(disposition: "attachment; filename*=x-unknown''" + text);
        }
    }

    // ------------------------------------------------------------------ usage counters

    private static string Format(SubscriptionUsage usage)
    {
        List<string> fields = [];
        if (usage.UploadBytes is { } upload) { fields.Add("upload=" + upload.ToString(CultureInfo.InvariantCulture)); }
        if (usage.DownloadBytes is { } download) { fields.Add("download=" + download.ToString(CultureInfo.InvariantCulture)); }
        if (usage.TotalBytes is { } total) { fields.Add("total=" + total.ToString(CultureInfo.InvariantCulture)); }
        if (usage.ExpireUnixSeconds is { } expire) { fields.Add("expire=" + expire.ToString(CultureInfo.InvariantCulture)); }
        return string.Join("; ", fields);
    }

    private static long? RandomCounter(Random random, long max) => random.Next(5) switch
    {
        0 => null,
        1 => 0,
        2 => max,
        3 => random.NextInt64(0, 1024),
        _ => random.NextInt64(0, max),
    };

    [Fact]
    public void Usage_FormatThenParseIsIdentityForEveryValidCombination()
    {
        const long MaxExpire = 253402300799;
        Random random = new(Seed + 3);
        int checkedCount = 0;
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            SubscriptionUsage usage = new(
                RandomCounter(random, long.MaxValue),
                RandomCounter(random, long.MaxValue),
                RandomCounter(random, long.MaxValue),
                RandomCounter(random, MaxExpire));
            if (usage is { UploadBytes: null, DownloadBytes: null, TotalBytes: null, ExpireUnixSeconds: null })
            {
                Assert.Null(SubscriptionUsage.Parse(string.Empty));
                continue;
            }

            Assert.Equal(usage, SubscriptionUsage.Parse(Format(usage)));
            checkedCount++;
        }

        Assert.True(checkedCount > 4000);
    }

    [Theory]
    [InlineData("upload=9223372036854775807", 9223372036854775807L)]
    [InlineData("upload=9223372036854775808", null)]
    [InlineData("upload=18446744073709551616", null)]
    [InlineData("upload=1e19", null)]
    [InlineData("upload=1e3", 1000L)]
    [InlineData("upload=1.9", 1L)]
    [InlineData("upload=0.999", 0L)]
    [InlineData("upload=-0", 0L)]
    [InlineData("upload=-1", null)]
    [InlineData("upload=-0.5", null)]
    [InlineData("upload= 7 ", 7L)]
    [InlineData("upload=+5", 5L)]
    [InlineData("upload=1,000", null)]
    [InlineData("upload=0x10", null)]
    [InlineData("upload=NaN", null)]
    [InlineData("upload=Infinity", null)]
    [InlineData("upload=", null)]
    [InlineData("upload", null)]
    [InlineData("upload=1 2", null)]
    [InlineData("upload=\u0663", null)]
    public void Usage_CounterValueTableCoversMissingNegativeAndOverflow(string header, long? expected)
    {
        // A second well-formed field keeps the whole header alive so the table isolates the counter parser.
        SubscriptionUsage? usage = SubscriptionUsage.Parse(header + "; total=1");
        Assert.NotNull(usage);
        Assert.Equal(expected, usage.UploadBytes);
        Assert.Equal(1, usage.TotalBytes);
    }

    [Fact]
    public void Usage_ExpiryBoundaryDropsOnlyTheExpiry()
    {
        Assert.Equal(new SubscriptionUsage(1, null, null, 253402300799), SubscriptionUsage.Parse("upload=1; expire=253402300799"));
        Assert.Equal(new SubscriptionUsage(1, null, null, null), SubscriptionUsage.Parse("upload=1; expire=253402300800"));
        Assert.Equal(new SubscriptionUsage(1, null, null, null), SubscriptionUsage.Parse("upload=1; expire=-1"));
        Assert.Equal(new SubscriptionUsage(null, null, null, 0), SubscriptionUsage.Parse("expire=0"));
    }

    [Fact]
    public void Usage_HeaderLengthBoundaryIsExact()
    {
        string prefix = "upload=1;";
        string atLimit = prefix + new string(' ', 8192 - prefix.Length);
        Assert.Equal(8192, atLimit.Length);
        Assert.Equal(new SubscriptionUsage(1, null, null, null), SubscriptionUsage.Parse(atLimit));
        Assert.Null(SubscriptionUsage.Parse(atLimit + " "));
        Assert.Null(SubscriptionUsage.Parse("   "));
        Assert.Null(SubscriptionUsage.Parse(null));
    }

    [Fact]
    public void Usage_NamesAreCaseInsensitiveWhitespaceTolerantAndLastValueWins()
    {
        Assert.Equal(new SubscriptionUsage(1, 2, 3, 4), SubscriptionUsage.Parse("UpLoAd=1;DOWNLOAD =2 ; \ttotal= 3;Expire=4"));
        Assert.Equal(new SubscriptionUsage(2, null, null, null), SubscriptionUsage.Parse("upload=1; upload=2"));
        Assert.Null(SubscriptionUsage.Parse("upload=1; upload=oops; total=1")!.UploadBytes);
        Assert.Equal(new SubscriptionUsage(1, null, null, null), SubscriptionUsage.Parse("upload=1; unknown=5; =3; ;;;"));
    }

    [Fact]
    public void Usage_FuzzedHeadersNeverThrowAndNeverProduceOutOfRangeCounters()
    {
        string[] pieces =
        [
            "upload", "download", "total", "expire", "UPLOAD", "=", ";", " ", "-", "+", ".", "e", "E", "0", "1", "9",
            "99999999999999999999", "253402300799", "253402300800", "\u0000", "\u00a0", "٣", "١", "NaN", "inf", ",", "x",
        ];
        Random random = new(Seed + 4);
        for (int iteration = 0; iteration < 8000; iteration++)
        {
            StringBuilder header = new();
            int count = random.Next(0, 30);
            for (int index = 0; index < count; index++)
            {
                header.Append(pieces[random.Next(pieces.Length)]);
            }

            SubscriptionUsage? usage = SubscriptionUsage.Parse(header.ToString());
            if (usage is null)
            {
                continue;
            }

            foreach (long? value in new[] { usage.UploadBytes, usage.DownloadBytes, usage.TotalBytes })
            {
                Assert.True(value is null or >= 0, header.ToString());
            }

            Assert.True(usage.ExpireUnixSeconds is null or (>= 0 and <= 253402300799), header.ToString());
        }
    }

    // ------------------------------------------------------------------ end to end through the catalog

    [Theory]
    [InlineData("profile-title: Provider Plan", "Provider Plan")]
    [InlineData("profile-title: base64:6K6i6ZiF", "订阅")]
    [InlineData("profile-title: base64:@@@@\r\nContent-Disposition: attachment; filename=\"Fallback.yaml\"", "Fallback")]
    [InlineData("Content-Disposition: attachment; filename*=UTF-8''%E8%AE%A2%E9%98%85.yaml", "订阅")]
    [InlineData("Content-Disposition: attachment; filename=\"dir/Named.yaml\"", "Named")]
    [InlineData("X-Unrelated: 1", null)]
    public async Task ProviderNameFlowsIntoAnAutomaticallyNamedLinkAndItsProfile(string headerBlock, string? expectedName)
    {
        await using CatalogHarness harness = new();
        List<(string, string)> headers = [];
        foreach (string line in headerBlock.Split("\r\n"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers.Add((line[..colon], line[(colon + 1)..].TrimStart()));
        }

        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(c, 200, "rules: []\n", headers: headers));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync(string.Empty, server.Url("/opaque?token=secret"), limit.Token);
        Assert.Equal("127.0.0.1", link.Name);
        Assert.True(link.UseProviderName);

        ProfileImportResult result = await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);

        string expected = expectedName ?? "127.0.0.1";
        Assert.Equal(expected, result.ProfileName);
        Assert.Equal(expected, harness.Service.GetSubscriptionLinks().Single().Name);
        Assert.Equal(expected, harness.Service.GetProfiles().Single(profile => profile.Id == result.ProfileId).Name);
        // A secret path or query is never promoted to a display name.
        Assert.DoesNotContain("secret", result.ProfileName, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque", result.ProfileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Subscription-Userinfo", "upload=10; download=20; total=1000; expire=1893456000", 10L, 20L, 1000L, 1893456000L)]
    [InlineData("Subscription-Userinfo", "upload=-5; download=oops; total=1e30; expire=999999999999999", null, null, null, null)]
    [InlineData("Subscription-Userinfo", "total=5", null, null, 5L, null)]
    public async Task UsageHeaderIsCommittedWithTheImportAndMalformedFieldsStayUnknown(
        string header, string value, long? upload, long? download, long? total, long? expire)
    {
        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(c, 200, "rules: []\n", headers: [(header, value)]));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);

        await harness.Service.ImportSubscriptionLinkAsync(link, limit.Token);

        SubscriptionUsage? usage = harness.Service.GetSubscriptionLinks().Single().Usage;
        if (upload is null && download is null && total is null && expire is null)
        {
            Assert.Null(usage);
        }
        else
        {
            Assert.Equal(new SubscriptionUsage(upload, download, total, expire), usage);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("999999999999999999999")]
    [InlineData("abc")]
    public async Task ProviderUpdateIntervalHeaderCannotChangeTheUserOwnedInterval(string headerValue)
    {
        await using CatalogHarness harness = new();
        await using LoopbackHttpServer server = new((c, _) => Reply.TextAsync(
            c, 200, "rules: []\n", headers: [("Profile-Update-Interval", headerValue)]));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", server.Url(), limit.Token);
        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "P", link.Uri, true, 12, limit.Token));
        ProfileSubscriptionLink current = harness.Service.GetSubscriptionLinks().Single();

        await harness.Service.ImportSubscriptionLinkAsync(current, limit.Token);

        ProfileSubscriptionLink after = harness.Service.GetSubscriptionLinks().Single();
        Assert.Equal(12, after.UpdateIntervalHours);
        DateTimeOffset now = DateTimeOffset.Now;
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(now.AddHours(11)), due => due.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(now.AddHours(13)), due => due.Id == link.Id);
    }
}
