<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**基于 mihomo 内核的 Windows 原生代理客户端**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#安装)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · **简体中文** · [繁體中文](./README-zh-Hant.md) · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 尚在开发中，目前没有正式发布版本。CI 生成的安装包为未签名的测试构建，不适用于日常使用。

Clash# 是基于 [mihomo](https://github.com/MetaCubeX/mihomo) 内核的 Windows 原生代理客户端。主程序采用 C#、.NET 10 与 WinUI 3 开发，以 MSIX 包分发，由独立的 WPF 安装器负责安装、修复与卸载。适用于 Windows 11 x64，以及带桌面体验的 Windows Server 2025。

除订阅、配置、节点与规则的管理外，Clash# 还处理 Windows 平台上的几类具体问题：系统代理的接管与还原，整机透明代理（TUN），WSL、终端与 Microsoft Store 应用的代理访问，以及程序异常退出后遗留的代理设置。

## 功能

| 方面 | 说明 |
| :--- | :--- |
| **界面** | WinUI 3 原生控件、Fluent 图标与 Windows 11 材质，支持浅色、深色与系统主题色。主控页采用磁贴布局，磁贴可显示、隐藏与排序。 |
| **启动检查** | 启动代理前检查外部 mihomo 进程、端口占用、Windows 手动代理及其他 TUN/VPN 网卡，并对可安全处理的项目提供修复。 |
| **代理还原** | 退出时还原系统代理；异常退出时由恢复进程立即还原；启动时检查并清理残留的代理设置。 |
| **Windows 网络修正** | 分别诊断 WSL、终端与 Microsoft Store 的代理访问，逐项应用或撤销修正。 |
| **触发器** | 条件全部满足时，按顺序执行预设操作。条件包括流量、速率、连接数、运行时间与系统时间；操作包括切换模式、断开连接、发送通知与退出程序。 |
| **统计与日志** | 流量、连接、节点健康与规则命中记录保存于本地 SQLite 数据库。 |
| **界面语言** | 简体中文、繁體中文、English、한국어、Русский、Français、Deutsch、فارسی（从右到左布局）。 |

## 模式

Clash# 以模式对网络的作用为其命名。与常见客户端的对应关系如下：

| Clash# | 常见名称 | 行为 |
| :--- | :--- | :--- |
| **未启用** | 关闭 | 停止内核，还原系统代理。 |
| **待命** | 直连 | 内核运行，流量默认直连。 |
| **按规则接管** | 规则 | 按配置中的规则分流。 |
| **接管所有** | 全局 | 全部流量经由所选代理。 |

透明代理（TUN）由 **设置 › 代理** 中的「透明代理」控制，默认开启。开启且 Mihomo 服务可用时，接管通过 TUN 完成，不读取系统代理的程序同样受其覆盖；Mihomo 服务未部署时，接管改由 Windows 系统代理完成。

> [!CAUTION]
> 透明代理接管整台计算机的路由与 DNS。Clash# 按「一台计算机、一个交互用户」设计，不提供多用户会话之间的流量隔离。

启用 **设置 › 任务栏托盘** 中的「启用托盘色彩状态指示」后，托盘图标以颜色区分当前状态：

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="灰色托盘图标" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="绿色托盘图标" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="紫色托盘图标" /> |
| :---: | :---: | :---: |
| 未启用或待命 | 系统代理 | 透明代理 |

## 安装

**系统要求**：Windows 11 x64，或带桌面体验的 Windows Server 2025。

正式版本发布后，安装包提供于 [Releases](https://github.com/Water-Run/ClashSharp/releases) 页面。

1. 下载并解压发布包。`ClashSharp-Installer.exe` 须与同目录的 `payload` 文件夹保持在一起。
2. 在当前用户会话中运行 `ClashSharp-Installer.exe`。应用安装于运行安装器的用户名下，无需以管理员身份运行，也无需预装 .NET。
3. 部署 Mihomo 服务及必要的证书信任时，安装器请求管理员授权。授权前应核对所显示的发布者。

修复、升级与卸载均通过再次运行同一安装器完成。

> [!WARNING]
> 卸载应通过 `ClashSharp-Installer.exe` 进行。仅在 Windows 的「设置 › 应用」中移除时，Mihomo 服务等机器级资源不会同步清理。

## 快速上手

1. 在 **代理 › 链接** 中添加订阅链接。Clash# 下载并校验对应配置。
2. 在 **代理 › 配置** 中确认该配置为当前配置。
3. 在 **主控** 中选择 **按规则接管**。
4. 在 **代理 › 节点** 中选择节点，可先进行延迟测试。

Clash# 每次启动时显示启动提示，汇总订阅、透明代理服务、启动还原与代理残留的检查结果。该提示可在 **设置 › 启动时** 中关闭。

本地混合端口默认为 `10000`，HTTP 与 SOCKS 共用。不读取系统代理的程序可直接使用 `127.0.0.1:10000`。端口可在 **设置 › 代理** 中修改。

关闭主窗口时，Clash# 默认最小化到托盘并继续运行。退出应使用托盘菜单中的「安全退出」；退出时默认停止内核并还原系统代理。

### 页面

| 页面 | 用途 |
| :--- | :--- |
| **主控** | 切换模式；以磁贴显示内核状态、速率、流量、延迟、订阅用量、公网 IP 等信息。 |
| **代理** | 节点、配置（含历史版本）、订阅链接与规则。 |
| **触发器** | 自动化任务，自上而下依次评估。 |
| **连接** | 活动连接及其所属进程、命中规则与代理路径。 |
| **统计数据** | 按配置、节点与时间汇总的长期统计；系统日志亦由此进入。 |
| **设置** | 语言与外观、启动、代理、Windows 原生修正、通知、托盘与数据备份。 |

## 常见问题

<details>
<summary><b>WSL、终端或 Microsoft Store 应用无法通过代理访问网络</b></summary>
<br />

上述程序不读取 Windows 系统代理。**设置 › Windows 原生** 可对相应项目执行诊断，并按结果应用修正；修正可随时撤销。开启透明代理后，此类程序的流量同样会被接管。
</details>

<details>
<summary><b>启动时提示冲突</b></summary>
<br />

冲突检查涵盖四类情况：外部 mihomo 进程、代理端口被占用、Windows 手动代理已开启、存在其他 TUN 或 VPN 网卡。对话框逐项说明，并对可安全处理的项目提供修复操作。TUN 与 VPN 网卡仅作提示，Clash# 不会自动禁用。检查可在 **设置 › 启动时** 中随时重新执行。
</details>

<details>
<summary><b>异常退出后，系统代理仍处于开启状态</b></summary>
<br />

Clash# 异常退出时，恢复进程会立即还原仍归 Clash# 所有的系统代理。若计算机在此之前已关闭，Clash# 将在下次启动时检查并清理残留。另可在 **设置 › 启动时** 中注册「启动还原辅助服务」，使清理在登录 Windows 时即执行，而不必等待 Clash# 启动。
</details>

<details>
<summary><b>地区名称或旗帜与配置不一致</b></summary>
<br />

**设置 › 中国大陆特色功能** 默认启用旗帜替换与文本补全，按中国大陆的规范调整界面中部分地区的名称与旗帜。该功能仅作用于界面显示，不修改配置、日志、搜索、复制内容与导出数据，可在同一位置关闭。
</details>

<details>
<summary><b>备份与更新</b></summary>
<br />

**设置 › 数据** 可将 Clash# 设置导出为单个文件，并可选择一并包含代理配置与订阅，供日后导入还原。日志可单独导出为 SQLite 数据库，不支持导入。

**关于** 页面检查 GitHub Releases 上的新版本。更新由安装器完成。
</details>

## 从源码构建

需要 Windows x64、PowerShell 7，以及 [`global.json`](./global.json) 中指定版本的 .NET SDK。

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

安装包的构建步骤见 [Installer/README.md](./ClashSharp/Installer/README.md)。

<details>
<summary><b>仓库结构</b></summary>
<br />

| 路径 | 内容 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 主程序 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 领域模型、应用逻辑，以及 Windows 与存储适配 |
| `ClashSharp/ClashSharp.MihomoService` | 为透明代理运行内核的 Windows 服务 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 异常退出后还原系统代理的恢复进程 |
| `ClashSharp/ClashSharp.Installer*` | WPF 安装器及其事务逻辑 |
| `ClashSharp/SandboxTest` | 基于 Windows 沙盒的安装包冒烟测试 |
| `docs/` | 设计记录与开发记录 |

</details>

编码约定见 [CodingStyle.md](./CodingStyle.md)。

## 许可

代理内核为 MetaCubeX 的 [mihomo](https://github.com/MetaCubeX/mihomo)，以 GPL-3.0 许可发布；GeoIP 与 GeoSite 数据来自 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)。

Clash# 以 [AGPL-3.0](./LICENSE) 许可开源。
