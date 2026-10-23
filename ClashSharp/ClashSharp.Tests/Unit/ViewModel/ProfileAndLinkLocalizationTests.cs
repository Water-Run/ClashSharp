using System.Globalization;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class ProfileAndLinkLifecycleViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_UpdatesKnownCatalogStatusesWithoutReloadingOrReplacingRows(AppLanguage language)
    {
        MutablePageLocalization localization = new()
        { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        ConfigurationProfile profile = CreateProfile("user-profile", false) with { Status = "可用" };
        ProfileSubscriptionLink link = CreateLink("subscription") with { Status = "已添加" };
        FakeProfileManagementCatalog profiles = new() { Profiles = [profile] };
        FakeSubscriptionLinkCatalog links = new() { Links = [link] };
        ModelDisplayMapper mapper = new(text => "display:" + text,
            profileStatusLocalizer: ProfileStatusText.Localize,
            subscriptionStatusLocalizer: SubscriptionStatusText.Localize);
        ProfilesViewModel profilePage = new(localization.GetString, profiles, new RecordingPageLog(),
            () => "user-profile", new TestApplicationErrorSink(), mapper);
        LinksViewModel linkPage = new(localization.GetString, links, new RecordingPageLog(), new TestApplicationErrorSink(), mapper);
        await profilePage.LoadAsync(CancellationToken.None);
        await linkPage.LoadAsync(CancellationToken.None);
        ConfigurationProfileDisplay profileRow = Assert.Single(profilePage.Profiles);
        ProfileSubscriptionLinkDisplay linkRow = Assert.Single(linkPage.SubscriptionLinks);
        profilePage.SelectedProfile = profileRow;
        linkPage.SelectedLink = linkRow;
        profilePage.RefreshLocalization();
        linkPage.RefreshLocalization();
        List<string?> profileChanges = [];
        List<string?> linkChanges = [];
        profileRow.PropertyChanged += (_, args) => profileChanges.Add(args.PropertyName);
        linkRow.PropertyChanged += (_, args) => linkChanges.Add(args.PropertyName);

        localization.Language = language;
        profilePage.RefreshLocalization();
        linkPage.RefreshLocalization();

        Assert.Equal("display:" + localization.GetString(ProfileStatusText.Available), profileRow.StatusDisplay);
        Assert.Equal("display:" + localization.GetString("ProfileCatalog.Status.Added"), linkRow.StatusDisplay);
        Assert.Contains(nameof(profileRow.StatusDisplay), profileChanges);
        Assert.Contains(nameof(linkRow.StatusDisplay), linkChanges);
        Assert.Same(profileRow, profilePage.SelectedProfile);
        Assert.Same(linkRow, linkPage.SelectedLink);
        Assert.Equal(profile, profileRow.Model);
        Assert.Equal(link, linkRow.Model);
        Assert.Equal("可用", profile.Status);
        Assert.Equal("已添加", link.Status);
        Assert.Equal("display:" + profile.Name, profileRow.NameDisplay);
        Assert.Equal("display:" + link.Uri, linkRow.UriDisplay);
        Assert.Equal(1, profiles.GetProfilesCallCount);
        Assert.Equal(1, links.GetLinksCallCount);
        Assert.Empty(links.DeletedIds);
    }

    [Fact]
    public void LocalizationRefresh_PreservesUnknownDiagnosticsAndUsesUpdatedProfileStatus()
    {
        MutablePageLocalization localization = new() { Language = AppLanguage.English };
        ModelDisplayMapper mapper = new(text => "display:" + text,
            profileStatusLocalizer: ProfileStatusText.Localize,
            subscriptionStatusLocalizer: SubscriptionStatusText.Localize);
        ConfigurationProfile profile = CreateProfile("profile", false) with { Status = ProfileStatusText.Available };
        ConfigurationProfileDisplay row = mapper.Map(profile);
        row.UpdateFrom(mapper.Map(profile with { Status = ProfileStatusText.Invalid }));
        string diagnostic = "Provider diagnostic: 可用 / HTTP 204 / {0}";
        ProfileSubscriptionLinkDisplay link = mapper.Map(CreateLink("link") with { Status = diagnostic })
            .WithDetails(localization.GetString);
        localization.Language = AppLanguage.German;

        row.RefreshLocalization(localization.GetString, mapper.MapText);
        link.RefreshLocalization(localization.GetString);

        Assert.Equal("display:" + localization.GetString(ProfileStatusText.Invalid), row.StatusDisplay);
        Assert.Equal("display:" + diagnostic, link.StatusDisplay);
        Assert.Equal(diagnostic, link.Model.Status);
    }

    [Fact]
    public void LocalizationRefresh_ReformatsKnownHttpStatusWithoutLosingItsCode()
    {
        MutablePageLocalization localization = new() { Language = AppLanguage.English };
        ModelDisplayMapper mapper = new(text => text, subscriptionStatusLocalizer: SubscriptionStatusText.Localize);
        ProfileSubscriptionLink model = CreateLink("link") with { Status = SubscriptionStatusText.CheckSucceeded(204) };
        ProfileSubscriptionLinkDisplay row = mapper.Map(model).WithDetails(localization.GetString);
        localization.Language = AppLanguage.Persian;

        row.RefreshLocalization(localization.GetString);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            localization.GetString("ProfileCatalog.Subscription.CheckSucceeded.Format"), 204), row.StatusDisplay);
        Assert.Equal(model, row.Model);
        Assert.Equal(SubscriptionStatusText.CheckSucceeded(204), model.Status);
    }

    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_PreservesSelectedProfilesLinksAndProviderContent(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        FakeProfileManagementCatalog profiles = new() { Profiles = [CreateProfile(ProfileIdentifiers.BuiltInDirect, true), CreateProfile("user-profile", false)] };
        FakeSubscriptionLinkCatalog links = new() { Links = [CreateLink("subscription")] };
        ProfilesViewModel profilePage = new(localization.GetString, profiles, new RecordingPageLog(),
            () => ProfileIdentifiers.BuiltInDirect, new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        LinksViewModel linkPage = new(localization.GetString, links, new RecordingPageLog(), new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await profilePage.LoadAsync(CancellationToken.None);
        await linkPage.LoadAsync(CancellationToken.None);
        ConfigurationProfileDisplay selectedProfile = profilePage.Profiles[0];
        ProfileSubscriptionLinkDisplay selectedLink = linkPage.SubscriptionLinks[0];
        profilePage.SelectedProfile = selectedProfile;
        linkPage.SelectedLink = selectedLink;
        string providerStatus = selectedLink.StatusDisplay;
        string uri = selectedLink.UriDisplay;
        localization.Language = language;

        profilePage.RefreshLocalization();
        linkPage.RefreshLocalization();

        Assert.Same(selectedProfile, profilePage.SelectedProfile);
        Assert.Same(selectedLink, linkPage.SelectedLink);
        Assert.Equal(localization.GetString("ProfileCatalog.BuiltInDirect.Name"), selectedProfile.NameDisplay);
        Assert.Equal("user-profile", profilePage.Profiles[1].NameDisplay);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, localization.GetString("Links.Schedule.Automatic.Format"), selectedLink.UpdateIntervalHours), selectedLink.ScheduleDisplay);
        Assert.Equal(providerStatus, selectedLink.StatusDisplay);
        Assert.Equal(uri, selectedLink.UriDisplay);
        Assert.Equal(1, profiles.GetProfilesCallCount);
        Assert.Equal(1, links.GetLinksCallCount);
        Assert.Empty(links.DeletedIds);
    }
}
