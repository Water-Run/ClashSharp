extern alias ClashSharpUi;

using CloseBehaviorMode = ClashSharp.Model.CloseBehaviorMode;
using WindowCloseBehaviorPolicy =
    ClashSharpUi::ClashSharp.Service.WindowCloseBehaviorPolicy;
using WindowCloseDisposition =
    ClashSharpUi::ClashSharp.Service.WindowCloseDisposition;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies close behavior never hides a window without a usable tray entry.</summary>
public sealed class WindowCloseBehaviorPolicyTests
{
    [Fact]
    public void Resolve_MinimizeToTrayWithoutAvailableTray_RequestsSafeExit()
    {
        WindowCloseDisposition disposition = WindowCloseBehaviorPolicy.Resolve(
            CloseBehaviorMode.MinimizeToTray,
            isTrayAvailable: false);

        Assert.Equal(WindowCloseDisposition.RequestSafeExit, disposition);
    }

    [Fact]
    public void Resolve_MinimizeToTrayWithAvailableTray_HidesWindow()
    {
        WindowCloseDisposition disposition = WindowCloseBehaviorPolicy.Resolve(
            CloseBehaviorMode.MinimizeToTray,
            isTrayAvailable: true);

        Assert.Equal(WindowCloseDisposition.HideToTray, disposition);
    }

    [Fact]
    public void Resolve_ExitWithoutConfirmation_DoesNotDependOnTrayAvailability()
    {
        Assert.Equal(
            WindowCloseDisposition.RequestSafeExit,
            WindowCloseBehaviorPolicy.Resolve(
                CloseBehaviorMode.ExitWithoutConfirmation,
                isTrayAvailable: false));
        Assert.Equal(
            WindowCloseDisposition.RequestSafeExit,
            WindowCloseBehaviorPolicy.Resolve(
                CloseBehaviorMode.ExitWithoutConfirmation,
                isTrayAvailable: true));
    }

    [Fact]
    public void Resolve_ConfirmExit_DoesNotDependOnTrayAvailability()
    {
        Assert.Equal(
            WindowCloseDisposition.ConfirmExit,
            WindowCloseBehaviorPolicy.Resolve(
                CloseBehaviorMode.ConfirmExit,
                isTrayAvailable: false));
        Assert.Equal(
            WindowCloseDisposition.ConfirmExit,
            WindowCloseBehaviorPolicy.Resolve(
                CloseBehaviorMode.ConfirmExit,
                isTrayAvailable: true));
    }
}
