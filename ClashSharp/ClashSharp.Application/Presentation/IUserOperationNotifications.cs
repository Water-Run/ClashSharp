namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Publishes localized operation outcomes under the user's notification policy.</summary>
public interface IUserOperationNotifications
{
    /// <summary>Reports one committed success or operational failure without exposing private inputs.</summary>
    /// <param name="titleResourceKey">Application-authored localization key for the operation category.</param>
    /// <param name="messageResourceKey">Application-authored localization key for the outcome.</param>
    /// <param name="failed">Whether the operation failed.</param>
    /// <param name="arguments">Non-sensitive application-authored message parameters.</param>
    void ReportOperation(string titleResourceKey, string messageResourceKey, bool failed, params object[] arguments);

    /// <summary>Reports loss or recovery of application availability under the important/default policies.</summary>
    /// <param name="titleResourceKey">Localized category key.</param>
    /// <param name="messageResourceKey">Localized availability outcome key.</param>
    /// <param name="failed">True for an availability failure, false for recovery.</param>
    void ReportAvailability(string titleResourceKey, string messageResourceKey, bool failed) =>
        ReportOperation(titleResourceKey, messageResourceKey, failed);
}
