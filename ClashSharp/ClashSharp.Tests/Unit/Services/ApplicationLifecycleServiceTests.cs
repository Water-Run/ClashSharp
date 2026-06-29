/*
 * Application Lifecycle Service Tests
 * Verifies Settings uses a service boundary for exit and restart commands
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ApplicationLifecycleServiceTests.cs
 * @date: 2026-06-29
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class ApplicationLifecycleServiceTests
{
    [Fact]
    public void ExitApplication_ShutsDownRuntimeBeforeExiting()
    {
        List<string> calls = [];
        ApplicationLifecycleService service = CreateService(calls);

        service.ExitApplication();

        Assert.Equal(["shutdown", "exit"], calls);
    }

    [Fact]
    public void RestartApplication_ShutsDownStartsCurrentExecutableThenExits()
    {
        List<string> calls = [];
        ApplicationLifecycleService service = CreateService(calls);

        service.RestartApplication();

        Assert.Equal(["shutdown", @"start:C:\Apps\ClashSharp.exe", "exit"], calls);
    }

    private static ApplicationLifecycleService CreateService(List<string> calls)
    {
        return new ApplicationLifecycleService(
            new FakeRuntimeShutdown(calls),
            new FakeProcessLauncher(calls),
            new FakeApplicationExit(calls),
            () => @"C:\Apps\ClashSharp.exe");
    }

    private sealed class FakeRuntimeShutdown(List<string> calls) : IApplicationLifecycleRuntimeShutdown
    {
        public void Shutdown()
        {
            calls.Add("shutdown");
        }
    }

    private sealed class FakeProcessLauncher(List<string> calls) : IApplicationLifecycleProcessLauncher
    {
        public void Start(string executablePath)
        {
            calls.Add($"start:{executablePath}");
        }
    }

    private sealed class FakeApplicationExit(List<string> calls) : IApplicationLifecycleExit
    {
        public void Exit()
        {
            calls.Add("exit");
        }
    }
}
