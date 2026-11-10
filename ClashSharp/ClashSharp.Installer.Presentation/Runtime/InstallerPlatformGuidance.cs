namespace ClashSharp.Installer.Runtime;

/// <summary>User-facing explanation and next step for one unsupported-platform diagnostic.</summary>
/// <param name="Title">Short status title.</param>
/// <param name="Detail">Why this computer cannot install Clash# and what to do instead.</param>
/// <param name="Requirement">Compact text for the system-requirement row.</param>
public sealed record InstallerPlatformGuidance(string Title, string Detail, string Requirement)
{
    /// <summary>The supported platforms, stated identically in every fallback message.</summary>
    public const string SupportedPlatforms = "Windows 11 或 Windows Server 2025 桌面体验（x64）";

    /// <summary>Maps a stable platform diagnostic to guidance; unknown codes receive the general requirement.</summary>
    /// <param name="diagnosticCode">Platform diagnostic produced by the installer platform policy.</param>
    /// <param name="text">Localized text; Simplified Chinese when omitted.</param>
    public static InstallerPlatformGuidance For(string? diagnosticCode, Presentation.InstallerText? text = null)
    {
        Presentation.InstallerText localized = text ?? new Presentation.InstallerText();
        InstallerPlatformGuidance source = ForSource(diagnosticCode);
        return new(localized[source.Title], localized.Format(source.Detail, localized[SupportedPlatforms]),
            localized.Format(source.Requirement, localized[SupportedPlatforms]));
    }

    private static InstallerPlatformGuidance ForSource(string? diagnosticCode) => diagnosticCode switch
    {
        "installer.environment.windows_11_required" => new(
            "Windows 版本过旧，无法安装",
            "此电脑运行的是 Windows 10。Clash# 需要 Windows 11（x64），旧系统缺少 Clash# 依赖的 Mica 材质、Windows 11 Fluent 界面等系统能力。Windows 10 主流版本已于 2025 年 10 月 14 日结束支持，不再提供常规安全更新。建议升级到 Windows 11 后再运行此安装器。",
            "此电脑为 Windows 10，系统过旧。需要 Windows 11 x64；Windows 10 已结束支持，建议升级。"),
        "installer.environment.windows_server_2025_required" => new(
            "Windows Server 版本过旧，无法安装",
            "Clash# 在服务器上需要 Windows Server 2025 或更高版本（桌面体验，x64），较早的 Windows Server 缺少 Clash# 依赖的 Mica 材质、Fluent 界面等系统能力。请升级到 Windows Server 2025，或改在 Windows 11 电脑上安装。",
            "此服务器版本过旧。需要 Windows Server 2025 桌面体验（x64）或 Windows 11。"),
        "installer.environment.desktop_experience_required" => new(
            "需要桌面体验",
            "此服务器以 Server Core 方式运行，没有桌面体验。Clash# 是图形界面应用，请在 Windows Server 2025 桌面体验或 Windows 11 上安装。",
            "不支持 Server Core。需要 Windows Server 2025 桌面体验或 Windows 11。"),
        "installer.environment.x64_os_required" => new(
            "需要 x64 版本的 Windows",
            "Clash# 及其内置代理核心只提供 x64 版本。此电脑的 Windows 不是 x64（例如 ARM64），请在 x64 电脑上安装。",
            "此电脑的 Windows 不是 x64。需要 x64 版本的 Windows 11 或 Windows Server 2025。"),
        "installer.environment.x64_process_required" => new(
            "安装器未以 x64 方式运行",
            "安装器必须作为原生 x64 进程运行。请完整解压安装包，直接运行其中的 Setup.exe，不要通过兼容层或其他外壳启动。",
            "安装器进程不是 x64。请直接运行解压目录中的 Setup.exe。"),
        _ => new(
            "当前系统不支持安装",
            "Clash# 需要 {0}；不支持 Server Core。",
            "需要 {0}。"),
    };
}
