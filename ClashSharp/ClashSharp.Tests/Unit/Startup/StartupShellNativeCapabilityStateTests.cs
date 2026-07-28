extern alias ClashSharpUi;

using StartupShellNativeCapabilityState =
    ClashSharpUi::ClashSharp.Hosting.Startup.StartupShellNativeCapabilityState;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies optional native shell features degrade independently from the WinUI window.</summary>
public sealed class StartupShellNativeCapabilityStateTests
{
    [Fact]
    public void AcquireWindowHandle_OrdinaryFailure_LeavesNativeFeaturesUnavailable()
    {
        StartupShellNativeCapabilityState state = new();
        bool titleBarAttempted = false;
        bool trayAttempted = false;

        bool acquired = state.TryAcquireWindowHandle(
            () => throw new InvalidOperationException("native handle unavailable"));
        bool titleBarInitialized = state.TryRunWindowHandleFeature(
            _ => titleBarAttempted = true);
        bool trayInitialized = state.TryCreateWindowMessageFeature(
            _ =>
            {
                trayAttempted = true;
                return new object();
            },
            out object? tray);

        Assert.False(acquired);
        Assert.False(state.HasWindowHandle);
        Assert.False(state.HasWindowMessageHook);
        Assert.False(titleBarInitialized);
        Assert.False(trayInitialized);
        Assert.False(titleBarAttempted);
        Assert.False(trayAttempted);
        Assert.Null(tray);
    }

    [Fact]
    public void InstallWindowMessageHook_OrdinaryFailure_PreservesHandleOnlyFeatures()
    {
        StartupShellNativeCapabilityState state = new();
        Assert.True(state.TryAcquireWindowHandle(() => new nint(41)));

        bool hookInstalled = state.TryInstallWindowMessageHook(
            _ => throw new InvalidOperationException("window procedure unavailable"));
        nint titleBarHandle = 0;
        bool titleBarInitialized = state.TryRunWindowHandleFeature(
            handle => titleBarHandle = handle);
        bool trayInitialized = state.TryCreateWindowMessageFeature(
            _ => new object(),
            out object? tray);

        Assert.False(hookInstalled);
        Assert.True(state.HasWindowHandle);
        Assert.False(state.HasWindowMessageHook);
        Assert.True(titleBarInitialized);
        Assert.Equal(new nint(41), titleBarHandle);
        Assert.False(trayInitialized);
        Assert.Null(tray);
    }

    [Fact]
    public void CreateWindowMessageFeature_OrdinaryFailure_IsContained()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();

        bool created = state.TryCreateWindowMessageFeature<object>(
            _ => throw new InvalidOperationException("tray unavailable"),
            out object? feature);

        Assert.False(created);
        Assert.Null(feature);
        Assert.True(state.HasWindowHandle);
        Assert.True(state.HasWindowMessageHook);
    }

    [Fact]
    public void CreateWindowMessageFeature_Success_UsesAcquiredWindowHandle()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();
        nint observedHandle = 0;

        bool created = state.TryCreateWindowMessageFeature(
            handle =>
            {
                observedHandle = handle;
                return new object();
            },
            out object? feature);

        Assert.True(created);
        Assert.NotNull(feature);
        Assert.Equal(new nint(41), observedHandle);
    }

    [Fact]
    public void CreateWindowMessageFeature_NullResult_RemainsUnavailable()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();

        bool created = state.TryCreateWindowMessageFeature<object>(
            static _ => null!,
            out object? feature);

        Assert.False(created);
        Assert.Null(feature);
    }

    [Fact]
    public void TryReleaseWindowMessageHook_RestoreReturnsFalse_RetainsCapabilities()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();
        int restoreAttempts = 0;

        bool released = state.TryReleaseWindowMessageHook(
            (_, _) =>
            {
                restoreAttempts++;
                return false;
            });

        Assert.False(released);
        Assert.Equal(1, restoreAttempts);
        Assert.True(state.HasWindowHandle);
        Assert.True(state.HasWindowMessageHook);
        Assert.Equal(new nint(41), state.WindowHandle);
        Assert.Equal(new nint(73), state.PreviousWindowProcedure);
    }

    [Fact]
    public void TryReleaseWindowMessageHook_OrdinaryRestoreFailure_RetainsCapabilities()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();

        bool released = state.TryReleaseWindowMessageHook(
            (_, _) => throw new InvalidOperationException("restore unavailable"));

        Assert.False(released);
        Assert.True(state.HasWindowHandle);
        Assert.True(state.HasWindowMessageHook);
    }

    [Fact]
    public void TryReleaseWindowMessageHook_RestoreSucceeds_ClearsCapabilities()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();
        nint restoredHandle = 0;
        nint restoredProcedure = 0;

        bool released = state.TryReleaseWindowMessageHook(
            (handle, previousWindowProcedure) =>
            {
                restoredHandle = handle;
                restoredProcedure = previousWindowProcedure;
                return true;
            });

        Assert.True(released);
        Assert.Equal(new nint(41), restoredHandle);
        Assert.Equal(new nint(73), restoredProcedure);
        Assert.False(state.HasWindowHandle);
        Assert.False(state.HasWindowMessageHook);
        Assert.Equal(nint.Zero, state.WindowHandle);
        Assert.Equal(nint.Zero, state.PreviousWindowProcedure);
    }

    [Fact]
    public void NativeOperations_CancellationIsNotContained()
    {
        StartupShellNativeCapabilityState state = CreateMessageCapableState();
        OperationCanceledException expected = new();

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => state.TryCreateWindowMessageFeature<object>(
                _ => throw expected,
                out _));

        Assert.Same(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ProcessFatalExceptions))]
    public void NativeOperations_ProcessFatalFailureIsNotContained(Exception expected)
    {
        StartupShellNativeCapabilityState state = new();

        Exception actual = Assert.Throws(
            expected.GetType(),
            () => state.TryAcquireWindowHandle(() => throw expected));

        Assert.Same(expected, actual);
    }

    public static TheoryData<Exception> ProcessFatalExceptions => new()
    {
        CreateException<OutOfMemoryException>(),
        CreateException<StackOverflowException>(),
        CreateException<AccessViolationException>(),
    };

    private static StartupShellNativeCapabilityState CreateMessageCapableState()
    {
        StartupShellNativeCapabilityState state = new();
        Assert.True(state.TryAcquireWindowHandle(() => new nint(41)));
        Assert.True(state.TryInstallWindowMessageHook(_ => new nint(73)));
        return state;
    }

    private static TException CreateException<TException>()
        where TException : Exception =>
        Assert.IsType<TException>(Activator.CreateInstance<TException>());
}
