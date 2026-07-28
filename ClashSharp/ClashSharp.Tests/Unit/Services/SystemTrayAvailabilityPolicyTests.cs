extern alias ClashSharpUi;

using SystemTrayAvailabilityPolicy =
    ClashSharpUi::ClashSharp.Service.SystemTrayAvailabilityPolicy;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies tray availability is revalidated before the main window may be hidden.</summary>
public sealed class SystemTrayAvailabilityPolicyTests
{
    [Fact]
    public void TryRegisterRecoveryMessage_NonzeroMessage_EnablesRecovery()
    {
        bool registered = SystemTrayAvailabilityPolicy.TryRegisterRecoveryMessage(
            static () => 0xC001,
            out uint message);

        Assert.True(registered);
        Assert.Equal(0xC001u, message);
    }

    [Fact]
    public void TryRegisterRecoveryMessage_ZeroFailureSentinel_DisablesRecovery()
    {
        bool registered = SystemTrayAvailabilityPolicy.TryRegisterRecoveryMessage(
            static () => 0,
            out uint message);

        Assert.False(registered);
        Assert.Equal(0u, message);
    }

    [Fact]
    public void TryRegisterRecoveryMessage_OrdinaryNativeFailure_DisablesRecovery()
    {
        bool registered = SystemTrayAvailabilityPolicy.TryRegisterRecoveryMessage(
            static () => throw new InvalidOperationException("registration unavailable"),
            out uint message);

        Assert.False(registered);
        Assert.Equal(0u, message);
    }

    [Fact]
    public void CanHideToTray_MissingRecoveryMessage_DoesNotProbeOrHide()
    {
        int probes = 0;

        bool canHide = SystemTrayAvailabilityPolicy.CanHideToTray(
            recoveryMessage: 0,
            () =>
            {
                probes++;
                return true;
            });

        Assert.False(canHide);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void CanHideToTray_RecoveryMessageAndAvailableIcon_AllowsHide()
    {
        bool canHide = SystemTrayAvailabilityPolicy.CanHideToTray(
            recoveryMessage: 0xC001,
            static () => true);

        Assert.True(canHide);
    }

    [Fact]
    public void TryEnsureAvailable_ModifySucceeds_DoesNotAddDuplicateIcon()
    {
        int addAttempts = 0;

        bool available = SystemTrayAvailabilityPolicy.TryEnsureAvailable(
            static () => true,
            () =>
            {
                addAttempts++;
                return true;
            });

        Assert.True(available);
        Assert.Equal(0, addAttempts);
    }

    [Fact]
    public void TryEnsureAvailable_ModifyFailsAndAddSucceeds_RecoversExplorerLoss()
    {
        List<string> operations = [];

        bool available = SystemTrayAvailabilityPolicy.TryEnsureAvailable(
            () =>
            {
                operations.Add("modify");
                return false;
            },
            () =>
            {
                operations.Add("add");
                return true;
            });

        Assert.True(available);
        Assert.Equal(["modify", "add"], operations);
    }

    [Fact]
    public void TryEnsureAvailable_ModifyAndAddFail_ReportsUnavailable()
    {
        bool available = SystemTrayAvailabilityPolicy.TryEnsureAvailable(
            static () => false,
            static () => false);

        Assert.False(available);
    }

    [Fact]
    public void TryEnsureAvailable_OrdinaryNativeFailure_ReportsUnavailable()
    {
        bool available = SystemTrayAvailabilityPolicy.TryEnsureAvailable(
            static () => throw new InvalidOperationException("shell unavailable"),
            static () => false);

        Assert.False(available);
    }

    [Fact]
    public void TryEnsureAvailable_CancellationIsNotContained()
    {
        OperationCanceledException expected = new();

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => SystemTrayAvailabilityPolicy.TryEnsureAvailable(
                () => throw expected,
                static () => true));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void TryRefreshAndPreserveReachability_TrayRecovers_DoesNotRestoreVisibleWindow()
    {
        bool windowRestored = false;

        bool available = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            static () => true,
            wasWindowHiddenToTray: true,
            () => windowRestored = true);

        Assert.True(available);
        Assert.False(windowRestored);
    }

    [Fact]
    public void TryRefreshAndPreserveReachability_TrayUnavailableWhileHidden_RestoresWindow()
    {
        bool windowRestored = false;

        bool available = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            static () => false,
            wasWindowHiddenToTray: true,
            () => windowRestored = true);

        Assert.False(available);
        Assert.True(windowRestored);
    }

    [Fact]
    public void TryRefreshAndPreserveReachability_TrayUnavailableWhileVisible_DoesNotStealFocus()
    {
        bool windowRestored = false;

        bool available = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            static () => false,
            wasWindowHiddenToTray: false,
            () => windowRestored = true);

        Assert.False(available);
        Assert.False(windowRestored);
    }

    [Fact]
    public void TryRefreshAndPreserveReachability_OrdinaryTrayFailureWhileHidden_RestoresWindow()
    {
        bool windowRestored = false;

        bool available = SystemTrayAvailabilityPolicy.TryRefreshAndPreserveReachability(
            static () => throw new InvalidOperationException("shell unavailable"),
            wasWindowHiddenToTray: true,
            () => windowRestored = true);

        Assert.False(available);
        Assert.True(windowRestored);
    }
}
