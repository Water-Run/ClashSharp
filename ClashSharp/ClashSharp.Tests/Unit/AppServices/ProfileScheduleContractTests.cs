using System.Globalization;
using System.Reflection;
using System.Security;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Strings;

namespace ClashSharp.Tests.Unit.AppServices;

/// <summary>
/// Contracts of the subscription schedule (backoff, enablement, interval edits), subscription URL identity and
/// the language-stable status vocabulary that the catalog persists.
/// </summary>
public sealed class ProfileScheduleContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 8, 0, 0, TimeSpan.FromHours(8));

    // ------------------------------------------------------------------ scheduler pass classification

    private sealed class ThrowingCatalog(IReadOnlyList<ProfileSubscriptionLink> links, Func<string, Exception?> failure)
        : IProfileSubscriptionSchedulerCatalog
    {
        public List<string> Attempted { get; } = [];

        public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now) => links;

        public Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(
            ProfileSubscriptionLink link, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Attempted.Add(link.Id);
            if (failure(link.Id) is { } exception)
            {
                throw exception;
            }

            return Task.FromResult<ProfileImportResult?>(
                new ProfileImportResult("subscription-" + link.Id, link.Name, "x.yaml", 1, 1, "ok"));
        }
    }

    private static ProfileSubscriptionLink Link(string id) =>
        new(id, id, "https://example.invalid/" + id, true, 24, DateTimeOffset.MinValue, "added", 1);

    public static TheoryData<string> RecoverableFailureNames() =>
    [
        .. RecoverableFailures().Select(static pair => pair.Name),
    ];

    /// <summary>Every failure family the catalog's own download, decode, history and persistence code can raise.</summary>
    private static IEnumerable<(string Name, Func<Exception> Create)> RecoverableFailures()
    {
        yield return (nameof(HttpRequestException), () => new HttpRequestException("503"));
        yield return (nameof(IOException), () => new IOException("disk"));
        yield return (nameof(FileNotFoundException), () => new FileNotFoundException("gone"));
        yield return (nameof(DirectoryNotFoundException), () => new DirectoryNotFoundException("gone"));
        yield return (nameof(InvalidDataException), () => new InvalidDataException("profile configuration must be valid UTF-8"));
        yield return (nameof(UnauthorizedAccessException), () => new UnauthorizedAccessException("denied"));
        yield return (nameof(SecurityException), () => new SecurityException("denied"));
        yield return (nameof(InvalidOperationException), () => new InvalidOperationException("stale"));
        yield return (nameof(ArgumentException), () => new ArgumentException("bad"));
        yield return (nameof(ArgumentOutOfRangeException), () => new ArgumentOutOfRangeException("p"));
        yield return (nameof(ProfileValidationException), () => new ProfileValidationException(MissingProfileSections.Rules));
        yield return (nameof(AggregateException), () => new AggregateException("both", new IOException("a"), new IOException("b")));
        yield return (nameof(TaskCanceledException), () => new TaskCanceledException("provider timeout"));
    }

    [Theory]
    [MemberData(nameof(RecoverableFailureNames))]
    public async Task Pass_EveryRecoverableLinkFailureIsLoggedAndTheRemainingLinksStillRun(string name)
    {
        Func<Exception> create = RecoverableFailures().Single(pair => pair.Name == name).Create;
        ThrowingCatalog catalog = new([Link("a"), Link("b"), Link("c")], id => id == "a" ? create() : null);
        List<(string Level, string? Detail)> logs = [];
        ProfileSubscriptionScheduler scheduler = new(catalog, TimeProvider.System, (level, _, _, detail) => logs.Add((level, detail)));

        await scheduler.UpdateDueSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(["a", "b", "c"], catalog.Attempted);
        Assert.Single(logs, log => log.Level == "Warning" && log.Detail!.StartsWith("a:", StringComparison.Ordinal));
        Assert.Equal(2, logs.Count(log => log.Level == "Info"));
    }

    [Fact]
    public async Task Pass_AdmissionCancellationEndsThePassWithoutWarningsOrFurtherWork()
    {
        using CancellationTokenSource admission = new();
        await admission.CancelAsync();
        ThrowingCatalog catalog = new([Link("a"), Link("b")], id => id == "a"
            ? new ProfileCatalogMutationCanceledException(new OperationCanceledException(admission.Token), admission.Token)
            : null);
        List<string> levels = [];
        ProfileSubscriptionScheduler scheduler = new(catalog, TimeProvider.System, (level, _, _, _) => levels.Add(level));

        await scheduler.UpdateDueSubscriptionsAsync(CancellationToken.None);

        Assert.Equal(["a"], catalog.Attempted);
        Assert.Empty(levels);
    }

    // ------------------------------------------------------------------ persisted backoff

    private static readonly double[] ExpectedBackoffMinutes = [5, 10, 20, 40, 80, 160, 320, 360, 360, 360, 360, 360];

    [Fact]
    public async Task Backoff_IsFiveMinutesDoublingToASixHourCapAndNeverShrinksOrOverflows()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        for (int failure = 1; failure <= 40; failure++)
        {
            DateTimeOffset attempt = T0.AddSeconds(failure);
            await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, attempt, CancellationToken.None);
            double minutes = ExpectedBackoffMinutes[Math.Min(failure, ExpectedBackoffMinutes.Length) - 1];
            DateTimeOffset due = attempt.AddMinutes(minutes);

            Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(due - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
            Assert.Contains(harness.Service.GetDueSubscriptionLinks(due), item => item.Id == link.Id);
        }
    }

    [Fact]
    public async Task Backoff_SuccessResetsTheFailureCountAndSchedulesAWholeInterval()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        for (int failure = 1; failure <= 6; failure++)
        {
            await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, T0, CancellationToken.None);
        }

        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, true, T0.AddHours(1), CancellationToken.None);

        DateTimeOffset due = T0.AddHours(1).AddHours(24);
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(due - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(due), item => item.Id == link.Id);

        // The next failure starts again from the shortest delay.
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, T0.AddHours(30), CancellationToken.None);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(T0.AddHours(30).AddMinutes(5)), item => item.Id == link.Id);
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(T0.AddHours(30).AddMinutes(5) - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
    }

    [Fact]
    public async Task Backoff_SurvivesAProcessRestart()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, T0, CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, T0, CancellationToken.None);

        await harness.ReopenAsync();

        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(T0.AddMinutes(10) - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(T0.AddMinutes(10)), item => item.Id == link.Id);
    }

    [Fact]
    public async Task Outcome_ForAnUnknownLinkChangesNothingAndBlankIdsAreRejected()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);

        await harness.Service.RecordSubscriptionUpdateOutcomeAsync("missing", false, T0, CancellationToken.None);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Service.RecordSubscriptionUpdateOutcomeAsync(" ", true, T0, CancellationToken.None));

        Assert.Contains(harness.Service.GetDueSubscriptionLinks(DateTimeOffset.Now.AddSeconds(1)), item => item.Id == link.Id);
    }

    // ------------------------------------------------------------------ enablement and interval edits

    [Fact]
    public async Task DisabledLinksAreNeverDueAndReenablingRestoresTheScheduleWithoutLosingTheLastSuccess()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, true, T0, CancellationToken.None);

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "P", link.Uri, false, 24, CancellationToken.None));
        Assert.Empty(harness.Service.GetDueSubscriptionLinks(T0.AddYears(50)));

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "P", link.Uri, true, 24, CancellationToken.None));
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(T0.AddHours(24) - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(T0.AddHours(24)), item => item.Id == link.Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(24 * 365)]
    public async Task IntervalEdit_RecomputesTheNextAttemptFromTheLastSuccess(int hours)
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, true, T0, CancellationToken.None);

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "P", link.Uri, true, hours, CancellationToken.None));

        Assert.Equal(hours, harness.Service.GetSubscriptionLinks().Single().UpdateIntervalHours);
        Assert.DoesNotContain(harness.Service.GetDueSubscriptionLinks(T0.AddHours(hours) - TimeSpan.FromTicks(1)), item => item.Id == link.Id);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(T0.AddHours(hours)), item => item.Id == link.Id);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(24 * 365 + 1)]
    [InlineData(int.MaxValue)]
    public async Task IntervalEdit_OutOfRangeIsRejectedAndLeavesTheLinkUntouched(int hours)
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        ProfileSubscriptionLink before = harness.Service.GetSubscriptionLinks().Single();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "Changed", link.Uri, true, hours, CancellationToken.None));

        Assert.Equal(before, harness.Service.GetSubscriptionLinks().Single());
    }

    [Fact]
    public async Task AddressChange_MakesTheLinkDueImmediatelyDespiteEarlierSuccessAndFailure()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, true, DateTimeOffset.Now, CancellationToken.None);
        await harness.Service.RecordSubscriptionUpdateOutcomeAsync(link.Id, false, DateTimeOffset.Now, CancellationToken.None);
        long revision = harness.Service.GetSubscriptionLinks().Single().Revision;

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "P", "https://example.invalid/other", true, 24, CancellationToken.None));

        ProfileSubscriptionLink after = harness.Service.GetSubscriptionLinks().Single();
        Assert.Equal(revision + 1, after.Revision);
        Assert.Contains(harness.Service.GetDueSubscriptionLinks(DateTimeOffset.Now.AddSeconds(1)), item => item.Id == link.Id);
    }

    [Fact]
    public async Task EveryDefinitionChangeBumpsTheRevisionSoInFlightSnapshotsAreRejected()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("P", "https://example.invalid/p", CancellationToken.None);
        long previous = link.Revision;
        foreach ((string name, string uri, bool enabled, int hours) in new[]
        {
            ("Q", "https://example.invalid/p", true, 24),
            ("Q", "https://example.invalid/p2", true, 24),
            ("Q", "https://example.invalid/p2", false, 24),
            ("Q", "https://example.invalid/p2", false, 12),
        })
        {
            Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, name, uri, enabled, hours, CancellationToken.None));
            long current = harness.Service.GetSubscriptionLinks().Single().Revision;
            Assert.True(current > previous);
            previous = current;
        }

        Assert.False(await harness.Service.TryUpdateSubscriptionLinkAsync("missing", "Q", "https://example.invalid/x", true, 24, CancellationToken.None));
        Assert.False(await harness.Service.TryDeleteSubscriptionLinkAsync("missing", CancellationToken.None));
        Assert.True(await harness.Service.TryDeleteSubscriptionLinkAsync(link.Id, CancellationToken.None));
        Assert.Empty(harness.Service.GetSubscriptionLinks());
        Assert.Empty(harness.Service.GetDueSubscriptionLinks(T0.AddYears(50)));
    }

    [Fact]
    public async Task ARenamedProviderNamedLinkStopsFollowingTheProviderOnlyWhenTheNameActuallyChanges()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync(string.Empty, "https://example.invalid/p", CancellationToken.None);
        Assert.True(link.UseProviderName);

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, link.Name, link.Uri, true, 12, CancellationToken.None));
        Assert.True(harness.Service.GetSubscriptionLinks().Single().UseProviderName);

        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(link.Id, "  Mine  ", link.Uri, true, 12, CancellationToken.None));
        ProfileSubscriptionLink renamed = harness.Service.GetSubscriptionLinks().Single();
        Assert.Equal("Mine", renamed.Name);
        Assert.False(renamed.UseProviderName);
    }

    // ------------------------------------------------------------------ URL identity

    private static readonly string[] BaseUrls =
    [
        "https://example.com/sub?token=AbC",
        "http://example.com:80/sub",
        "https://example.com:443/",
        "https://[::1]:8443/x?y=1",
        "https://\u7528\u6237:p%40ss@host.example/\u8def\u5f84?q=\u503c",
        "https://example.com/a%20b/c",
        "https://m\u00fcnchen.example/p",
    ];

    private static IEnumerable<string> Variants(string url)
    {
        Uri uri = new(url);
        yield return url;
        yield return uri.AbsoluteUri;
        yield return uri.AbsoluteUri + "#fragment";
        yield return uri.AbsoluteUri.Replace(uri.Scheme + "://", uri.Scheme.ToUpperInvariant() + "://", StringComparison.Ordinal);
        yield return uri.AbsoluteUri.Replace(uri.IdnHost, uri.IdnHost.ToUpperInvariant(), StringComparison.Ordinal);
        yield return "  " + url + "  ";
    }

    [Fact]
    public void UrlIdentity_IsAnEquivalenceRelationWhoseClassesAreFragmentSchemeAndHostInsensitive()
    {
        List<(int Class, string Url)> universe = [];
        for (int index = 0; index < BaseUrls.Length; index++)
        {
            foreach (string variant in Variants(BaseUrls[index]))
            {
                universe.Add((index, variant.Trim()));
            }
        }

        foreach ((int leftClass, string left) in universe)
        {
            Assert.True(ProfileSubscriptionLink.HasSameUri(left, left), "reflexive " + left);
            foreach ((int rightClass, string right) in universe)
            {
                bool same = ProfileSubscriptionLink.HasSameUri(left, right);
                Assert.Equal(same, ProfileSubscriptionLink.HasSameUri(right, left));
                if (leftClass == rightClass)
                {
                    Assert.True(same, $"variants of one URL must be identical: '{left}' vs '{right}'");
                }
                else
                {
                    Assert.False(same, $"different subscriptions must stay distinct: '{left}' vs '{right}'");
                }
            }
        }

        // Transitivity over a sample of the universe.
        string[] sample = [.. universe.Select(static pair => pair.Url).Distinct(StringComparer.Ordinal)];
        foreach (string a in sample)
        {
            foreach (string b in sample)
            {
                if (!ProfileSubscriptionLink.HasSameUri(a, b)) { continue; }
                foreach (string c in sample)
                {
                    if (ProfileSubscriptionLink.HasSameUri(b, c))
                    {
                        Assert.True(ProfileSubscriptionLink.HasSameUri(a, c));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("https://example.com/sub?token=AbC", "https://example.com/sub?token=abc")]
    [InlineData("https://example.com/Sub", "https://example.com/sub")]
    [InlineData("https://example.com/sub", "https://example.com/sub/")]
    [InlineData("https://example.com/sub?a=1&b=2", "https://example.com/sub?b=2&a=1")]
    [InlineData("https://example.com/sub", "http://example.com/sub")]
    [InlineData("https://example.com/sub", "https://example.com:8443/sub")]
    [InlineData("https://example.com/sub", "https://example.com./sub")]
    [InlineData("https://user@example.com/sub", "https://example.com/sub")]
    [InlineData("https://example.com/sub?x=1", "https://example.com/sub")]
    [InlineData("https://example.com/a%2Fb", "https://example.com/a/b")]
    public void UrlIdentity_TokensPathQueryPortAndCredentialsAreSignificant(string left, string right)
    {
        Assert.False(ProfileSubscriptionLink.HasSameUri(left, right));
        Assert.False(ProfileSubscriptionLink.HasSameUri(right, left));
    }

    [Theory]
    [InlineData("", "https://example.com/")]
    [InlineData("not a url", "not a url")]
    [InlineData("/relative", "/relative")]
    [InlineData("https://example.com/", "")]
    public void UrlIdentity_UnparseableTextIsNeverIdenticalToAnything(string left, string right)
    {
        Assert.False(ProfileSubscriptionLink.HasSameUri(left, right));
    }

    public static TheoryData<string, bool> SchemeUniverse() => new()
    {
        { "http://example.com/p", true },
        { "https://example.com/p", true },
        { "HTTPS://EXAMPLE.COM/p", true },
        { "  https://example.com/p  ", true },
        { "https://user:pw@example.com/p", true },
        { "https://[2001:db8::1]:8443/p", true },
        { "ftp://example.com/p", false },
        { "file:///C:/profile.yaml", false },
        { "\\\\server\\share\\profile.yaml", false },
        { "ws://example.com/p", false },
        { "wss://example.com/p", false },
        { "mailto:a@example.com", false },
        { "data:text/plain,rules", false },
        { "javascript:alert(1)", false },
        { "//example.com/p", false },
        { "example.com/p", false },
        { "https://", false },
        { "https:///p", false },
        { "http:example.com", false },
        { string.Empty, false },
        { "   ", false },
    };

    [Theory]
    [MemberData(nameof(SchemeUniverse))]
    public async Task AddLink_AcceptsExactlyAbsoluteHttpAndHttpsAddresses(string uri, bool accepted)
    {
        await using CatalogHarness harness = new();

        if (accepted)
        {
            ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("n", uri, CancellationToken.None);
            Assert.True(Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? stored));
            Assert.True(stored.Scheme is "http" or "https");
            Assert.Single(harness.Service.GetSubscriptionLinks());
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.AddSubscriptionLinkAsync("n", uri, CancellationToken.None));
            Assert.Empty(harness.Service.GetSubscriptionLinks());
        }
    }

    [Fact]
    public async Task AddAndUpdate_RejectEveryVariantOfAnAddressThatAlreadyExists()
    {
        await using CatalogHarness harness = new();
        ProfileSubscriptionLink first = await harness.Service.AddSubscriptionLinkAsync("one", "https://example.com/sub?token=AbC", CancellationToken.None);
        ProfileSubscriptionLink second = await harness.Service.AddSubscriptionLinkAsync("two", "https://example.com/sub?token=other", CancellationToken.None);

        foreach (string variant in Variants("https://example.com/sub?token=AbC").Select(static text => text.Trim()))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.AddSubscriptionLinkAsync("dup", variant, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.TryUpdateSubscriptionLinkAsync(
                second.Id, "two", variant, true, 24, CancellationToken.None));
        }

        // Re-saving a link with its own address is not a duplicate of itself.
        Assert.True(await harness.Service.TryUpdateSubscriptionLinkAsync(first.Id, "one", first.Uri + "#frag", true, 24, CancellationToken.None));
        Assert.Equal(2, harness.Service.GetSubscriptionLinks().Count);
    }

    [Fact]
    public async Task AddLink_BlankNameFallsBackToTheHostAndNeverToASecretPath()
    {
        await using CatalogHarness harness = new();

        ProfileSubscriptionLink link = await harness.Service.AddSubscriptionLinkAsync("  ", "https://provider.example/s3cr3t/path?token=XYZ", CancellationToken.None);

        Assert.Equal("provider.example", link.Name);
        Assert.True(link.UseProviderName);
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.AddSubscriptionLinkAsync(null!, "https://x.example/", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.AddSubscriptionLinkAsync("n", null!, CancellationToken.None));
    }

    // ------------------------------------------------------------------ language-stable status vocabulary

    private static string[] SubscriptionKeys() =>
        (string[])typeof(SubscriptionStatusText)
            .GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static string[] ProfileKeys() =>
    [
        ProfileStatusText.Available,
        ProfileStatusText.Canceled,
        ProfileStatusText.Validated,
        ProfileStatusText.Invalid,
    ];

    [Fact]
    public void StatusVocabulary_NormalizeInvertsEveryLanguageAndRelocalizationReachesEveryOtherLanguage()
    {
        string[] languagesKeys = [.. LocalizationResources.Translations.Keys.Select(static language => language.ToString())];
        Assert.True(languagesKeys.Length >= 8);
        foreach (AppLanguage from in LocalizationResources.Translations.Keys)
        {
            foreach (AppLanguage to in LocalizationResources.Translations.Keys)
            {
                string Resolve(string key) => LocalizationResources.Translations[to][key];
                foreach (string key in SubscriptionKeys())
                {
                    string stored = LocalizationResources.Translations[from][key];
                    Assert.Equal(key, SubscriptionStatusText.Normalize(stored));
                    Assert.Equal(LocalizationResources.Translations[to][key], SubscriptionStatusText.Localize(stored, Resolve));
                    Assert.Equal(LocalizationResources.Translations[to][key], SubscriptionStatusText.Localize(key, Resolve));
                }

                foreach (string key in ProfileKeys())
                {
                    string stored = LocalizationResources.Translations[from][key];
                    Assert.Equal(key, ProfileStatusText.Normalize(stored));
                    Assert.Equal(LocalizationResources.Translations[to][key], ProfileStatusText.Localize(stored, Resolve));
                }
            }
        }
    }

    [Fact]
    public void StatusVocabulary_NoTwoDifferentStatesShareDisplayTextInOrAcrossLanguages()
    {
        // A collision would make Normalize ambiguous: one stored display string could be read back as two states.
        foreach (string[] family in new[] { SubscriptionKeys(), ProfileKeys() })
        {
            Dictionary<string, string> owner = new(StringComparer.Ordinal);
            foreach (AppLanguage language in LocalizationResources.Translations.Keys)
            {
                foreach (string key in family)
                {
                    string text = LocalizationResources.Translations[language][key];
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{language}/{key} is blank");
                    if (owner.TryGetValue(text, out string? existing))
                    {
                        Assert.True(existing == key, $"'{text}' is both '{existing}' and '{key}' ({language})");
                    }
                    else
                    {
                        owner[text] = key;
                    }
                }
            }
        }
    }

    [Fact]
    public void StatusVocabulary_CheckSucceededRoundTripsEveryCodeInEveryLanguageAndRejectsOutOfRangeCodes()
    {
        const string FormatKey = "ProfileCatalog.Subscription.CheckSucceeded.Format";
        foreach (AppLanguage language in LocalizationResources.Translations.Keys)
        {
            string Resolve(string key) => LocalizationResources.Translations[language][key];
            for (int code = 100; code <= 999; code++)
            {
                string stored = SubscriptionStatusText.CheckSucceeded(code);
                string display = string.Format(CultureInfo.CurrentCulture, Resolve(FormatKey), code);
                Assert.Equal(display, SubscriptionStatusText.Localize(stored, Resolve));
                Assert.Equal(stored, SubscriptionStatusText.Normalize(display));
                Assert.Equal(stored, SubscriptionStatusText.Normalize(stored));
            }

            foreach (int code in new[] { 0, 99, 1000, 12345, -200 })
            {
                string display = string.Format(CultureInfo.InvariantCulture, Resolve(FormatKey), code);
                Assert.Equal(display, SubscriptionStatusText.Normalize(display));
            }
        }
    }

    [Fact]
    public void StatusVocabulary_UnknownDiagnosticsPassThroughAndNullIsEmpty()
    {
        Func<string, string> identity = static key => key;
        foreach (string custom in new[] { "Custom diagnostic", "", " ", "ProfileCatalog.Status.Unknown", "\u202eRTL", "x|y" })
        {
            Assert.Equal(custom, SubscriptionStatusText.Normalize(custom));
            Assert.Equal(custom, SubscriptionStatusText.Localize(custom, identity));
            Assert.Equal(custom, ProfileStatusText.Normalize(custom));
            Assert.Equal(custom, ProfileStatusText.Localize(custom, identity));
        }

        Assert.Equal(string.Empty, SubscriptionStatusText.Normalize(null));
        Assert.Equal(string.Empty, ProfileStatusText.Normalize(null));
        Assert.Equal(string.Empty, SubscriptionStatusText.Localize(null, identity));
        Assert.Equal(string.Empty, ProfileStatusText.Localize(null, identity));
    }
}
