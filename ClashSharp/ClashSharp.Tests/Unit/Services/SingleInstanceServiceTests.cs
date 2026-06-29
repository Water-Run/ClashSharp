/*
 * Single Instance Service Tests
 * Verifies duplicate Clash# process detection and close delegation
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/SingleInstanceServiceTests.cs
 * @date: 2026-06-29
 */

using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public void CheckForOtherInstance_WhenMatchingExecutableExists_ReturnsConflict()
    {
        FakeSingleInstanceEnvironment environment = new()
        {
            CurrentProcessId = 20,
            CurrentExecutablePath = @"C:\Apps\ClashSharp.exe",
            Processes =
            [
                new SingleInstanceProcessInfo(20, "ClashSharp", @"C:\Apps\ClashSharp.exe"),
                new SingleInstanceProcessInfo(21, "ClashSharp", @"C:\Apps\ClashSharp.exe"),
                new SingleInstanceProcessInfo(22, "Other", @"C:\Apps\Other.exe"),
            ],
        };
        SingleInstanceService service = new(environment);

        SingleInstanceCheckResult result = service.CheckForOtherInstance();

        Assert.True(result.HasOtherInstance);
        Assert.Equal(21, result.Process?.ProcessId);
    }

    [Fact]
    public void CloseOtherInstance_WhenConflictExists_DelegatesToEnvironment()
    {
        FakeSingleInstanceEnvironment environment = new()
        {
            CurrentProcessId = 20,
            CurrentExecutablePath = @"C:\Apps\ClashSharp.exe",
            Processes = [new SingleInstanceProcessInfo(21, "ClashSharp", @"C:\Apps\ClashSharp.exe")],
        };
        SingleInstanceService service = new(environment);

        SingleInstanceCloseResult result = service.CloseOtherInstance(service.CheckForOtherInstance());

        Assert.True(result.WasClosed);
        Assert.Equal([21], environment.ClosedProcessIds);
    }

    private sealed class FakeSingleInstanceEnvironment : ISingleInstanceEnvironment
    {
        public int CurrentProcessId { get; init; }

        public string CurrentExecutablePath { get; init; } = string.Empty;

        public IReadOnlyList<SingleInstanceProcessInfo> Processes { get; init; } = [];

        public List<int> ClosedProcessIds { get; } = [];

        public IReadOnlyList<SingleInstanceProcessInfo> GetProcesses()
        {
            return Processes;
        }

        public void CloseProcess(int processId)
        {
            ClosedProcessIds.Add(processId);
        }
    }
}
