namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Abstracts the operations needed to recover and foreground the primary window.</summary>
public interface IPrimaryWindowActivationTarget
{
    /// <summary>Gets whether the window presenter is currently minimized.</summary>
    bool IsMinimized { get; }

    /// <summary>Makes a hidden window visible.</summary>
    void Show();

    /// <summary>Restores a minimized window.</summary>
    void Restore();

    /// <summary>Activates the visible window.</summary>
    void Activate();
}

/// <summary>Applies the canonical redirected-activation sequence.</summary>
public static class PrimaryWindowActivation
{
    /// <summary>Shows, restores when minimized, and activates the primary window.</summary>
    /// <param name="window">Primary window activation target.</param>
    public static void BringToFront(IPrimaryWindowActivationTarget window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Show();
        if (window.IsMinimized)
        {
            window.Restore();
        }

        window.Activate();
    }
}
