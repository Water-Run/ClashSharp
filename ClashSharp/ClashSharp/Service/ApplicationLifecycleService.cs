using System;
using System.Threading;
using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.Service;

/// <summary>Hands UI lifetime commands to the App-owned process lifetime without stopping host services inline.</summary>
internal sealed class ApplicationLifecycleService
{
    private static ApplicationLifecycleService? _instance;
    private readonly IApplicationLifetimeRequestSink _requests;

    public ApplicationLifecycleService(
        IApplicationLifetimeRequestSink requests,
        bool installAsPrimaryInstance = false)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        if (installAsPrimaryInstance
            && Interlocked.CompareExchange(ref _instance, this, null) is not null)
        {
            throw new InvalidOperationException("The primary application lifecycle service is already configured.");
        }
    }

#if UNIT_TESTS
    public static ApplicationLifecycleService Instance { get; } = new(new IgnoringLifetimeRequestSink());
#else
    public static ApplicationLifecycleService Instance => Volatile.Read(ref _instance)
        ?? throw new InvalidOperationException("Application lifecycle requests are unavailable before primary host startup.");
#endif

    public void ExitApplication()
    {
        RequestExit("settings");
    }

    public void RestartApplication()
    {
        RequestRestart("settings");
    }

    internal void RequestExit(string source)
    {
        _requests.TryRequest(ApplicationLifetimeRequest.Exit(source));
    }

    internal void RequestRestart(string source)
    {
        _requests.TryRequest(ApplicationLifetimeRequest.Restart(source));
    }

#if UNIT_TESTS
    private sealed class IgnoringLifetimeRequestSink : IApplicationLifetimeRequestSink
    {
        public bool TryRequest(ApplicationLifetimeRequest request)
        {
            return true;
        }
    }
#endif
}
