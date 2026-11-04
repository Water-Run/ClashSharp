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

    /// <summary>Restores the editable draft to the same recommended choices used for a fresh installation.</summary>
    public DelegateCommand ResetInstallPreferencesCommand { get; }

    /// <summary>Whether the editable draft differs from the recommended defaults.</summary>
    public bool HasCustomInstallPreferences => _installPreferences != InstallerPreferences.Default;

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
    public IReadOnlyList<string> NotificationPresetOptions { get; } = ["默认", "仅重要", "详细", "关闭"];

    /// <summary>Explains the selected notification policy using the application's terminology.</summary>
    public string NotificationPresetDescription => _installPreferences.Notifications switch
    {
        InstallerNotificationPreset.CriticalOnly => "只提示内核、服务不可用和启动检查等关键问题。",
        InstallerNotificationPreset.All => "在默认级别上，增加导入、保存、测速等成功反馈，前台时也发送通知。",
        InstallerNotificationPreset.Disabled => "不发送系统通知，应用内仍保留操作结果。",
        _ => "提示关键问题，以及在后台发生的操作失败、测试超时和代理模式变化；前台时由窗口显示。",
    };

    /// <summary>Explains whether the selected installation includes transparent-proxy support.</summary>
    public string TransparentProxyServiceDescription => InstallTransparentProxyService
        ? "为透明代理（TUN）提供系统服务支持；代理模式由应用控制。"
        : "本次不安装此服务；透明代理（TUN）需要该组件支持。";

    /// <summary>Explains the current MCP choice without implying additional control permissions.</summary>
    public string McpServerDescription => EnableMcpServer
        ? "应用运行时提供只读接入；代理控制仍需在应用中单独授权。"
        : "供 Agent 读取运行状态，默认关闭，可稍后在应用中开启。";

    /// <summary>Lists the concrete initial preferences the selected environment preset writes for new settings.</summary>
    public IReadOnlyList<InstallerPreferenceDetail> SystemDefaultsDetails => FollowSystemDefaults
        ?
        [
            new("界面语言", "跟随 Windows 显示语言"),
            new("时间格式", "跟随 Windows 区域设置"),
            new("容量单位", "KB · 1024 进制"),
            new("速率单位", "字节/秒（B/s）"),
            new("中国大陆功能", "按 Windows 国家或地区自动选择"),
        ]
        :
        [
            new("界面语言", "English"),
            new("时间格式", "跟随 Windows 区域设置"),
            new("容量单位", "KB · 1024 进制"),
            new("速率单位", "字节/秒（B/s）"),
            new("中国大陆功能", "关闭"),
        ];

    /// <summary>Explains which initial environment preset is selected.</summary>
    public string SystemDefaultsDescription => FollowSystemDefaults
        ? "按 Windows 初始化语言、时钟、单位和地区功能；已有偏好保持。"
        : "使用英语、24 小时制等通用初始偏好；已有设置保持。";
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
        NotifyPreferenceDescriptions();
        if (property == nameof(InstallTransparentProxyService))
        {
            OnPropertyChanged(nameof(IsOwnerTransferActionVisible));
            OwnerTransferCommand.NotifyCanExecuteChanged();
        }
    }

    private void ResetInstallPreferences()
    {
        if (!CanEditInstallPreferences || !HasCustomInstallPreferences) { return; }
        _installPreferences = InstallerPreferences.Default;
        foreach (string property in new[] { nameof(CreateDesktopShortcut), nameof(RequestTaskbarPin), nameof(LaunchAtStartup),
                     nameof(RegisterProxyRecovery), nameof(InstallTransparentProxyService), nameof(FollowSystemDefaults),
                     nameof(EnableMcpServer), nameof(NotificationPresetIndex), nameof(IsOwnerTransferActionVisible) })
        {
            OnPropertyChanged(property);
        }
        NotifyPreferenceDescriptions();
        OwnerTransferCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPreferenceDescriptions()
    {
        OnPropertyChanged(nameof(HasCustomInstallPreferences));
        OnPropertyChanged(nameof(NotificationPresetDescription));
        OnPropertyChanged(nameof(TransparentProxyServiceDescription));
        OnPropertyChanged(nameof(McpServerDescription));
        OnPropertyChanged(nameof(SystemDefaultsDescription));
        OnPropertyChanged(nameof(SystemDefaultsDetails));
        ResetInstallPreferencesCommand.NotifyCanExecuteChanged();
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
        if (result.PreferencesWarnings.HasFlag(InstallerPreferencesWarnings.NativePinConfirmationPending)) { detail.Add("首次打开 Clash# 后，将在 Windows 支持的情况下请求任务栏固定确认。"); }
        StatusDetail = string.Join(" ", detail);
    }
}
