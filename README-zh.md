# `Clash#`

[English](./README.md)

![Clash# Logo](./ClashSharp/ClashSharp/Assets/Logo.svg)

`Clash#` 是一个现代化的 Windows 原生代理客户端，基于 [mihomo](https://github.com/MetaCubeX/mihomo) 构建。
`Clash#` 以`AGPL-3.0`协议开源于[GitHub](https://github.com/Water-Run/ClashSharp).

> 开发状态（2026-09-08）：1.0.0 正在开发。WinUI 页面与磁贴、共享备份/恢复、连接筛选、日志入库边界和设置校验已有自动化验证，当前 CI 的 4557 项测试全部通过；现有 Clash 配置的 32/32 节点探测与 HTTPS 转发成功。生产安装引擎已在 Windows Sandbox 完成安装、修复、卸载、八项中断/文件占用恢复、服务句柄占用恢复及实际系统重启后的恢复。已安装服务还通过了真实核心重载、有界核心崩溃恢复、显式重启服务宿主后的会话隔离和运行中核心卸载检查。开发安装器已在无全局 .NET 环境通过载荷校验。正式签名构建启用生产运行时，开发包保留执行门禁；WPF 交互和完整发布矩阵仍待完成。对应候选、证据和剩余工作见 [1.0.0 执行账本](./docs/reviews/1.0.0-execution-ledger.md)。

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

正式版本发布后，从 [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases) 下载发布包，解压后在普通用户会话中直接运行带 Authenticode 签名的 `ClashSharp-Installer.exe`。应用包归属当前用户；安装器在配置机器服务及必要的机器证书信任时请求 UAC，请核对已验证的发布者。保留 EXE 与相邻的 `payload` 目录。WPF 安装器自身为绿色自包含程序，无需预装 .NET；当前 CI 开发产物仅用于验证，尚非正式发布包。

> 修复、升级和完整卸载请重新运行 `ClashSharp-Installer.exe`。安装器会在证书/MSIX 被消费期间持续持有只读锁，并在使用前后复核同一文件对象的身份与 SHA-256；部署完成后还会依据签名 block map 逐项复核全部包作者文件，MSIX 同时启用 Windows package-integrity enforcement。不要只从 Windows 应用管理移除 MSIX，否则机器级 Service 资源可能无法同步清理。

正式构建的依赖解析与 payload 装配保持离线：所有 .NET 项目都使用预先完成的 locked restore，构建不会联网追踪 Mihomo `latest`。仓库内固定的版本、长度和 SHA-256 必须与普通二进制完全一致，并须先通过 `Tools\Prepare-GeoData.ps1` 准备四项固定 GeoData 资产。每次打包都会使用全新随机 staging，只接纳最终 manifest 声明的唯一 x64 Windows App Runtime 依赖，并要求通过 `CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT` 固定其受控 signer thumbprint。正式产物还要求受控的 MSIX 证书、可信且带时间戳的 Installer Authenticode 签名，以及显式的 `CLASHSHARP_WINDOWS_SDK_VERSION`；SignTool 只接受该固定 Windows Kits x64 目录中通过 Microsoft 签名信任校验的版本，签名阶段仅联系显式配置的 HTTPS 时间戳服务。WPF Installer 只在可清理的 staging 中发布为单个自包含可执行文件；精确文件集合、长度与 SHA-256 契约复核一致后，才会提升到 `artifacts\installer\release`。`build.ps1 -Development` 只生成明确标记为不可发布的未签名开发产物。

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

> 其中，透明代理需要在设置中打开。TUN 会接管整台机器的路由与 DNS；Clash# 当前按“一台机器、一个交互用户、一个 Core 所有者”设计，不支持多用户会话隔离。正式安装器开放执行后，更换所有者必须由目标用户重新运行 Installer，并在 Repair 中明确确认重新关联；普通 Repair 不会隐式换绑。

`Clash#`预设的默认端口是`10000`.

## 进阶使用

进阶用户可以配置透明代理、后台连接采样、配置导入与校验、节点延迟测试、Windows 原生修复动作、SQLite 日志清理和中国大陆显示策略。
