using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    private readonly OperationNotificationRunner _operationNotifications;
    private readonly IUserOperationNotifications? _userNotifications;

    /// <summary>Gets the notification content table text "Title".</summary>
    public string NotificationMatrixTitleText => _getString("Settings.NotificationMatrix.Title");

    /// <summary>Gets the notification content table text "Description".</summary>
    public string NotificationMatrixDescriptionText => _getString("Settings.NotificationMatrix.Description");

    /// <summary>Gets the notification content table text "CriticalTitle".</summary>
    public string NotificationMatrixCriticalTitleText => _getString("Settings.NotificationMatrix.CriticalTitle");

    /// <summary>Gets the notification content table text "CriticalDescription".</summary>
    public string NotificationMatrixCriticalDescriptionText => _getString("Settings.NotificationMatrix.CriticalDescription");

    /// <summary>Gets the notification content table text "ImportantTitle".</summary>
    public string NotificationMatrixImportantTitleText => _getString("Settings.NotificationMatrix.ImportantTitle");

    /// <summary>Gets the notification content table text "ImportantDescription".</summary>
    public string NotificationMatrixImportantDescriptionText => _getString("Settings.NotificationMatrix.ImportantDescription");

    /// <summary>Gets the notification content table text "RoutineTitle".</summary>
    public string NotificationMatrixRoutineTitleText => _getString("Settings.NotificationMatrix.RoutineTitle");

    /// <summary>Gets the notification content table text "RoutineDescription".</summary>
    public string NotificationMatrixRoutineDescriptionText => _getString("Settings.NotificationMatrix.RoutineDescription");

    /// <summary>Gets the notification content table text "TriggerTitle".</summary>
    public string NotificationMatrixTriggerTitleText => _getString("Settings.NotificationMatrix.TriggerTitle");

    /// <summary>Gets the notification content table text "TriggerDescription".</summary>
    public string NotificationMatrixTriggerDescriptionText => _getString("Settings.NotificationMatrix.TriggerDescription");

    private Task ResetSettingsAsync(SettingsResetScope scope, CancellationToken cancellationToken) =>
        _operationNotifications.ExecuteAsync(() => ResetSettingsCoreAsync(scope, cancellationToken),
            "Nav.Settings", "Notification.Settings.Reset", "Application.UnexpectedError", cancellationToken);
}
