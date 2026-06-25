/*
 * Runtime Shutdown Service Tests
 * Verifies shutdown cleanup through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/RuntimeShutdownServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for runtime shutdown cleanup.</summary>
public sealed class RuntimeShutdownServiceTests
{
    /// <summary>Verifies shutdown stops sampling and core before restoring Windows proxy when configured.</summary>
    [Fact]
    public void ShutdownRuntime_WhenRestoreProxyEnabled_StopsRuntimeAndDisablesWindowsProxy()
    {
        List<string> calls = [];
        RuntimeShutdownService service = CreateService(
            calls: calls,
            settings: new FakeRuntimeShutdownSettings { RestoreProxyOnExit = true });

        service.ShutdownRuntime();

        Assert.Equal(["sampling.stop", "core.stop", "proxy.disable"], calls);
    }

    /// <summary>Verifies shutdown does not mutate Windows proxy when restore-on-exit is disabled.</summary>
    [Fact]
    public void ShutdownRuntime_WhenRestoreProxyDisabled_DoesNotDisableWindowsProxy()
    {
        List<string> calls = [];
        RuntimeShutdownService service = CreateService(
            calls: calls,
            settings: new FakeRuntimeShutdownSettings { RestoreProxyOnExit = false });

        service.ShutdownRuntime();

        Assert.Equal(["sampling.stop", "core.stop"], calls);
    }

    /// <summary>Verifies cleanup failures are logged with localized text and do not escape shutdown.</summary>
    [Fact]
    public void ShutdownRuntime_WhenCleanupFails_LogsLocalizedWarning()
    {
        InvalidOperationException exception = new("stop failed");
        FakeRuntimeShutdownLog log = new();
        RuntimeShutdownService service = CreateService(
            core: new FakeRuntimeShutdownCore([], exception),
            log: log);

        service.ShutdownRuntime();

        RuntimeLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("Shutdown", entry.Category);
        Assert.Equal("localized cleanup failed", entry.Message);
        Assert.Equal("stop failed", entry.Detail);
    }

    private static RuntimeShutdownService CreateService(
        List<string>? calls = null,
        FakeRuntimeShutdownSettings? settings = null,
        FakeRuntimeShutdownCore? core = null,
        FakeRuntimeShutdownLog? log = null)
    {
        calls ??= [];
        return new RuntimeShutdownService(
            new FakeRuntimeShutdownSampling(calls),
            core ?? new FakeRuntimeShutdownCore(calls),
            settings ?? new FakeRuntimeShutdownSettings { RestoreProxyOnExit = true },
            new FakeRuntimeShutdownWindowsProxy(calls),
            log ?? new FakeRuntimeShutdownLog(),
            key => key == "RuntimeShutdown.CleanupFailed" ? "localized cleanup failed" : key);
    }

    private sealed class FakeRuntimeShutdownSampling(List<string> calls) : IRuntimeShutdownSampling
    {
        public void Stop()
        {
            calls.Add("sampling.stop");
        }
    }

    private sealed class FakeRuntimeShutdownCore(List<string> calls, Exception? exception = null) : IRuntimeShutdownCore
    {
        public void Stop()
        {
            calls.Add("core.stop");
            if (exception is not null)
            {
                throw exception;
            }
        }
    }

    private sealed class FakeRuntimeShutdownSettings : IRuntimeShutdownSettings
    {
        public bool RestoreProxyOnExit { get; init; }
    }

    private sealed class FakeRuntimeShutdownWindowsProxy(List<string> calls) : IRuntimeShutdownWindowsProxy
    {
        public void DisableProxy()
        {
            calls.Add("proxy.disable");
        }
    }

    private sealed class FakeRuntimeShutdownLog : IRuntimeShutdownLog
    {
        public List<RuntimeLogEntry> Entries { get; } = [];

        public void AppendLog(string level, string category, string message, string? detail)
        {
            Entries.Add(new RuntimeLogEntry(level, category, message, detail));
        }
    }

    private readonly record struct RuntimeLogEntry(string Level, string Category, string Message, string? Detail);
}
