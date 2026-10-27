using System.Runtime.CompilerServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation;

public sealed partial class InstallerShellViewModel
{
    private InstallerPreferences _installPreferences = InstallerPreferences.Default;
    private bool _offersInstallPreferences;
    private bool _hasPendingUserPreferences;

    /// <summary>Command that completes only current-user setup after verified installation.</summary>
    public AsyncDelegateCommand CompletePreferencesCommand { get; private set; } = null!;

    /// <summary>Whether this exact release offers a pending user-setup action.</summary>
    public bool IsCompletePreferencesVisible => _hasPendingUserPreferences && CanStartOperation
        && _runtime is IInstallerPreferencesRuntime { SupportsInstallPreferences: true };

    /// <summary>Whether a fresh installation offers fully supported choices.</summary>
    public bool IsInstallPreferencesVisible => _offersInstallPreferences && CanExecuteMutations && IsOperationSummaryVisible;
    /// <summary>Whether the current preference draft can be edited.</summary>
    public bool CanEditInstallPreferences => IsInstallPreferencesVisible && CanStartOperation;
    /// <summary>Current-user desktop shortcut selection.</summary>
    public bool CreateDesktopShortcut { get => _installPreferences.CreateDesktopShortcut; set => SetInstallPreferences(_installPreferences with { CreateDesktopShortcut = value }); }
    /// <summary>Native taskbar confirmation request selection.</summary>
    public bool RequestTaskbarPin
    {
        get => _installPreferences.RequestTaskbarPin;
        set
        {
            SetInstallPreferences(_installPreferences with { RequestTaskbarPin = value });
        }
    }
    /// <summary>Login startup selection.</summary>
    public bool LaunchAtStartup { get => _installPreferences.LaunchAtStartup; set => SetInstallPreferences(_installPreferences with { LaunchAtStartup = value }); }
    /// <summary>Independent proxy recovery selection.</summary>
    public bool RegisterProxyRecovery { get => _installPreferences.RegisterProxyRecovery; set => SetInstallPreferences(_installPreferences with { RegisterProxyRecovery = value }); }
    /// <summary>Transparent proxy service installation selection.</summary>
    public bool InstallTransparentProxyService { get => _installPreferences.InstallTransparentProxyService; set => SetInstallPreferences(_installPreferences with { InstallTransparentProxyService = value }); }
    /// <summary>Windows environment preset selection for new settings.</summary>
    public bool FollowSystemDefaults { get => _installPreferences.FollowSystemDefaults; set => SetInstallPreferences(_installPreferences with { FollowSystemDefaults = value }); }
    /// <summary>Read-status-only local MCP activation selection.</summary>
    public bool EnableMcpServer { get => _installPreferences.EnableMcpServer; set => SetInstallPreferences(_installPreferences with { EnableMcpServer = value }); }
    /// <summary>Notification choice labels in stable enum order.</summary>
    public IReadOnlyList<string> NotificationPresetOptions { get; } = ["默认通知（推荐）", "仅关键通知", "全部通知", "关闭通知"];
    /// <summary>Selected notification preset index.</summary>
    public int NotificationPresetIndex
    {
        get => (int)_installPreferences.Notifications;
        set
        {
            if (Enum.IsDefined((InstallerNotificationPreset)value))
            {
                SetInstallPreferences(_installPreferences with { Notifications = (InstallerNotificationPreset)value });
            }
        }
    }

    private void SetInstallPreferences(InstallerPreferences preferences, [CallerMemberName] string? property = null)
    {
        if (!CanEditInstallPreferences || preferences == _installPreferences) { return; }
        preferences.Validate();
        _installPreferences = preferences;
        OnPropertyChanged(property);
    }

    private void ApplyPreferencesWarnings(InstallerExecutionResult result, InstallerOperation? operation)
    {
        bool pending = result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.UserSetupPending);
        _hasPendingUserPreferences = pending && operation != InstallerOperation.Uninstall;
        if (result.Outcome != InstallerExecutionOutcome.Succeeded || result.PreferencesWarnings == InstallerPreferencesWarnings.None) { return; }
        List<string> detail = [];
        if (pending)
        {
            StatusBadge = "待完成";
            StatusTitle = operation == InstallerOperation.Uninstall ? "卸载已完成，快捷入口尚未全部清理" : "应用已安装，选项尚未完成";
            detail.Add(operation == InstallerOperation.Uninstall
                ? "应用已移除。仍被占用的本程序快捷方式可稍后手动清理。"
                : "可重新检查并选择“完成安装选项”重试；已有安装保持可用。");
        }
        if (result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.StartupDenied)) { detail.Add("Windows 未启用应用自动启动；请在启动应用设置中确认。"); }
        if (result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.ProxyRecoveryDenied)) { detail.Add("Windows 未启用登录时代理恢复；可在应用设置中检查。"); }
        if (result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.ExistingSettingsPreserved)) { detail.Add("检测到已有应用设置，已保留原有偏好。"); }
        if (result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.NativePinConfirmationPending)) { detail.Add("首次打开 Clash# 后，将由 Windows 确认任务栏固定。"); }
        StatusDetail = string.Join(" ", detail);
    }
}
