extern alias ClashSharpUi;

using ShellNavigationRequest = ClashSharpUi::ClashSharp.Presentation.Navigation.ShellNavigationRequest;
using ShellNavigationService = ClashSharpUi::ClashSharp.Presentation.Navigation.ShellNavigationService;
using ShellRoute = ClashSharpUi::ClashSharp.Presentation.Navigation.ShellRoute;
using ShellRouteCatalog = ClashSharpUi::ClashSharp.Presentation.Navigation.ShellRouteCatalog;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Unit tests for the typed, window-scoped navigation boundary.</summary>
public sealed class ShellNavigationServiceTests
{
    [Fact]
    public void RouteCatalog_RoundTripsEveryRoute()
    {
        foreach (ShellRoute route in Enum.GetValues<ShellRoute>())
        {
            string tag = ShellRouteCatalog.GetTag(route);

            Assert.True(ShellRouteCatalog.TryParse(tag, out ShellRoute parsed));
            Assert.Equal(route, parsed);
        }
    }

    [Fact]
    public void RouteCatalog_RejectsUnknownTag()
    {
        Assert.False(ShellRouteCatalog.TryParse("Unknown", out _));
        Assert.False(ShellRouteCatalog.TryParse(null, out _));
    }

    [Theory]
    [InlineData(null, "Statistics")]
    [InlineData("Trigger", "Triggers")]
    [InlineData("Connections", "Statistics")]
    [InlineData("Unknown", "Statistics")]
    public void RouteCatalog_LogsSelectsItsParent(string? filter, string expectedTag)
    {
        Assert.Equal(expectedTag, ShellRouteCatalog.GetTag(ShellRouteCatalog.GetNavigationRoute(ShellRoute.Logs, filter)));
    }

    [Fact]
    public void RouteCatalog_TopLevelPagesKeepTheirOwnSelection()
    {
        foreach (ShellRoute route in Enum.GetValues<ShellRoute>().Where(route => route != ShellRoute.Logs))
        {
            Assert.Equal(route, ShellRouteCatalog.GetNavigationRoute(route, "Trigger"));
        }
    }

    [Fact]
    public void NavigationService_PublishesForwardAndBackIntents()
    {
        using ShellNavigationService navigation = new();
        List<ShellNavigationRequest> requests = [];
        navigation.NavigationRequested += requests.Add;

        navigation.Navigate(ShellRoute.Logs, "Trigger");
        navigation.GoBack(ShellRoute.Statistics);

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal(ShellRoute.Logs, request.Route);
                Assert.Equal("Trigger", request.Parameter);
                Assert.False(request.IsBackNavigation);
            },
            request =>
            {
                Assert.Equal(ShellRoute.Statistics, request.Route);
                Assert.Null(request.Parameter);
                Assert.True(request.IsBackNavigation);
            });
    }

    [Fact]
    public void Dispose_DetachesWindowSubscriber()
    {
        ShellNavigationService navigation = new();
        int requestCount = 0;
        navigation.NavigationRequested += _ => requestCount++;

        navigation.Dispose();
        navigation.Navigate(ShellRoute.Settings);

        Assert.Equal(0, requestCount);
    }
}
