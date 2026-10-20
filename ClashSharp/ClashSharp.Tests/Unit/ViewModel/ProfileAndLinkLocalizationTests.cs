using System.Globalization;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class ProfileAndLinkLifecycleViewModelTests
{
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
