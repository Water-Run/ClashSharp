# `Clash#`

[English](./README.md)

![Clash# Logo](./Logo.png)

`Clash#` 是一个现代化的 Windows 原生代理客户端，基于 [mihomo](https://github.com/MetaCubeX/mihomo) 构建。
`Clash#` 以`AGPL-3.0`协议开源于[GitHub](https://github.com/Water-Run/ClashSharp).

## 关于Windows原生

`Clash#`是Windows原生的. 这不止是技术上使用`C#`+`WinUI3`开发, 契合Fluent的页面设计, `.msix`打包, 还包括其提供的一系列特色功能. 这包括:

- 定制的安装, 卸载管理程序
- 启动时的代理冲突检测和修复
- 异常退出时由一次性 Recovery Watchdog 立即恢复仍归 Clash# 所有的系统代理；登录恢复助手仅作为下次登录兜底
- WSL, 终端和微软商店的快速网络修正
- 主控页使用类似 Windows 快捷设置的磁贴呈现状态与常用操作

以及其它的有关定制内容.

## 安装与快速上手

### 安装

从 [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases) 下载发布包，解压后直接运行带 Authenticode 签名的 `ClashSharp-Installer.exe`。不要手动“以管理员身份运行”；应用证书与 MSIX 始终安装到当前用户，仅在配置机器级本地服务时由安装器单独请求 UAC 确认。UAC 必须显示预期的已验证发布者，不能是“未知发布者”。

> 修复、升级和完整卸载请重新运行 `ClashSharp-Installer.exe`。不要只从 Windows 应用管理移除 MSIX，否则机器级 Service 资源可能无法同步清理。

正式构建的依赖解析与 payload 装配保持离线：`dotnet publish` 使用预先完成的 locked restore，Cargo 使用 frozen lock/cache；构建不会联网追踪 Mihomo `latest`。仓库内固定的版本、长度和 SHA-256 必须与普通二进制完全一致，并须先通过 `Tools\Prepare-GeoData.ps1` 准备四项固定 GeoData 资产。每次打包都会使用全新随机 staging，只接纳最终 manifest 声明的唯一 x64 Windows App Runtime 依赖，并要求通过 `CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT` 固定其受控 signer thumbprint。正式产物还要求受控的 MSIX 证书、可信且带时间戳的 Installer Authenticode 签名，以及显式的 `CLASHSHARP_WINDOWS_SDK_VERSION`；SignTool 只接受该固定 Windows Kits x64 目录中通过 Microsoft 签名信任校验的版本，签名阶段仅联系显式配置的 HTTPS 时间戳服务。未签名 Cargo 输出只存在于可清理的 staging 目录；精确文件集合、长度与 SHA-256 契约在 promotion 后复核一致，才会发布到 `target\release-artifacts`。`build.ps1 -Development` 只生成明确标记为不可发布的未签名开发产物。

### 快速上手

欲使用`Clash#`, 显然你需要一个`Clash`订阅.

## 概念

`Clash#`和主流的一些软件有些不同的概念. 大致可以通过以下表格映射:

| `Clash#`中的概念 | 主流软件中的概念 | 说明                    |
|------------------|------------------|-------------------------|
| 主控             | 概览 / 主页      | 核心控制页面            |
| 未激活           | 关闭             | 不开启代理              |
| 待命             | 直连             | 开启代理, 直连模式      |
| 按规则接管       | 规则             | 开启代理, 规则模式      |
| 接管所有         | 全局             | 开启代理, 全局模式      |
| 透明代理         | TUN模式          | 开启代理, 且使用TUN模式 |

> 其中，透明代理需要在设置中打开。TUN 会接管整台机器的路由与 DNS；Clash# 当前按“一台机器、一个交互用户、一个 Core 所有者”设计，不支持多用户会话隔离。需要更换所有者时，请由目标用户重新运行 ClashSharp 安装器进行修复/关联。

`Clash#`预设的默认端口是`10000`.

## 进阶使用

进阶用户可以配置透明代理、后台连接采样、配置导入与校验、节点延迟测试、Windows 原生修复动作、SQLite 日志清理和中国大陆显示策略。
