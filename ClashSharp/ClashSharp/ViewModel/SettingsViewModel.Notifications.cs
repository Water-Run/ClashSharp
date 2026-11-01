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

    private Task ResetSettingsAsync(SettingsResetScope scope, CancellationToken cancellationToken) =>
        _operationNotifications.ExecuteAsync(() => ResetSettingsCoreAsync(scope, cancellationToken),
            "Nav.Settings", "Notification.Settings.Reset", "Application.UnexpectedError", cancellationToken);
}
