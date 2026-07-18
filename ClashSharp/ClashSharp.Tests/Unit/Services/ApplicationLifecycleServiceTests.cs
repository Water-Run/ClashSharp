using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class ApplicationLifecycleServiceTests
{
    [Fact]
    public void ExitApplication_EnqueuesOuterLifetimeRequestAndReturns()
    {
        FakeLifetimeRequestSink sink = new();
        ApplicationLifecycleService service = new(sink);

        service.ExitApplication();

        ApplicationLifetimeRequest request = Assert.Single(sink.Requests);
        Assert.Equal(ApplicationLifetimeRequestKind.Exit, request.Kind);
    }

    [Fact]
    public void RestartApplication_EnqueuesRestartWithoutLaunchingAProcessInline()
    {
        FakeLifetimeRequestSink sink = new();
        ApplicationLifecycleService service = new(sink);

        service.RestartApplication();

        ApplicationLifetimeRequest request = Assert.Single(sink.Requests);
        Assert.Equal(ApplicationLifetimeRequestKind.Restart, request.Kind);
    }

    private sealed class FakeLifetimeRequestSink : IApplicationLifetimeRequestSink
    {
        public List<ApplicationLifetimeRequest> Requests { get; } = [];

        public bool TryRequest(ApplicationLifetimeRequest request)
        {
            Requests.Add(request);
            return true;
        }
    }
}
