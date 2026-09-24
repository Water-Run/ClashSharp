<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**为 Windows 而生的代理客户端，基于 [mihomo](https://github.com/MetaCubeX/mihomo) 内核。**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#安装)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · **简体中文** · [繁體中文](./README-zh-Hant.md) · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 仍在开发中，暂无正式版本。
> CI 产出的安装包是未签名的测试构建，不适合日常使用。

大多数代理客户端追求处处能跑；Clash# 只打算在一个系统上跑，并且跑好。

它用 C# 和 WinUI 3 写成，看上去就是 Windows 11 的一部分，而不像一位借住的客人。它也熟悉 Windows 用户迟早会遇到的那些麻烦：不理会系统代理的程序、自成一片网络的 WSL、连不上 `localhost` 的商店应用，还有客户端崩溃之后依然开着的系统代理。

你只需要一个 Clash 订阅，剩下的交给 Clash#。

## 亮点

- **像 Windows 自己的应用。** 原生控件、Fluent 图标、半透明材质，浅色深色随你，主题色也跟着你走。首页是一面仿照「快速设置」的磁贴墙：留下你在意的，拖到你顺手的位置。
- **来时怎样，走时还怎样。** 启动之前，Clash# 会先看看有没有挡路的：另一个 mihomo、被占用的端口、残留的手动代理、别的 VPN 网卡。万一它意外退出，一个小小的看门狗会立刻关掉它留下的系统代理。
- **补上 Windows 的老缺口。** 点一下，WSL、终端和 Microsoft Store 应用也能走代理。
- **该自动的时候自动。** 触发器就是一条条「当……就……」的小规则。比如：*本次运行流量超过 5 GB，就切到待命，再通知我一声。*
- **记得住。** 流量、连接和节点健康状况都保存在本地的 SQLite 里，统计页看到的不只是此时此刻。
- **说你的语言。** 简体中文、繁體中文、English、한국어、Русский、Français、Deutsch，以及从右向左排版的 فارسی。

## 模式

Clash# 按「对网络做了什么」来给模式起名。如果你用过别的 Clash 客户端，可以这样对照：

| Clash# 里叫 | 通常叫 | 会发生什么 |
| :--- | :--- | :--- |
| **未启用** | 关闭 | 内核停止，系统代理还原。 |
| **待命** | 直连 | 内核在运行，但流量直接出门。 |
| **按规则接管** | 规则 | 流量按配置里的规则分流。 |
| **接管所有** | 全局 | 所有流量都经由你选中的代理。 |

**透明代理**（即 TUN 模式）是 *设置 › 代理* 里的一个独立开关。它能接住每个程序的流量，连那些对系统代理视而不见的也不例外。万一它没能启动，Clash# 可以自动退回到系统代理。

> [!CAUTION]
> 透明代理会接管整台电脑的路由与 DNS。Clash# 按「一台电脑、一位登录用户」设计，不会把不同用户的流量彼此隔开。

在设置里打开「托盘色彩状态指示」，托盘图标一眼就能告诉你现在的状态：

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="灰色托盘图标" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="绿色托盘图标" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="紫色托盘图标" /> |
| :---: | :---: | :---: |
| 未启用或待命 | 系统代理 | 透明代理 |

## 安装

**系统要求：** Windows 11 x64，或带桌面体验的 Windows Server 2025。

1.0.0 正式发布后，安装包会出现在 [Releases](https://github.com/Water-Run/ClashSharp/releases) 页面。

1. 下载并解压。`ClashSharp-Installer.exe` 和 `payload` 文件夹要放在一起。
2. 用你自己的账户双击 `ClashSharp-Installer.exe`。Clash# 装给运行安装器的那个人，所以不必「以管理员身份运行」，也不必事先装 .NET。
3. Windows 会请求一次权限，用来安装透明代理所需的后台服务。确认发布者是你认得的那一位，再点允许。

以后想修复、升级或卸载，再运行一次同一个安装器即可。

> [!WARNING]
> 请用 `ClashSharp-Installer.exe` 卸载，而不是在 *设置 › 应用* 里移除。只从 Windows 里删掉应用，后台服务会被落下。

## 上手

1. 在 **代理 › 链接** 里添加订阅链接，Clash# 会取回它背后的配置。
2. 在 **代理 › 配置** 里确认选中的正是这份配置。
3. 回到 **主控**，选 **按规则接管**。现在你已经通过代理上网了。
4. 在 **代理 › 节点** 里挑一个节点，或者测一测延迟，找个快的。

每次启动，Clash# 都会弹出一张简短的检查清单，确认一切就绪。等你不再需要它，可以在 *设置 › 启动时* 关掉。

不理会系统代理的程序，可以直接指向 `127.0.0.1:10000`——HTTP 和 SOCKS 共用这一个端口。端口号可在 *设置 › 代理* 里修改。

### 各个页面

| 页面 | 用来做什么 |
| :--- | :--- |
| **主控** | 切换模式；磁贴显示网速、流量、延迟、订阅用量、公网 IP 等等。 |
| **代理** | 节点、配置（每份都有历史版本）、订阅链接和规则。 |
| **触发器** | 你的自动化规则，自上而下依次判断。 |
| **连接** | 每一条活动连接，连同它的进程、命中的规则和走过的代理路径。 |
| **统计数据** | 按配置、按节点、按时间累积的长期统计。 |
| **设置** | 语言与外观、启动、透明代理、Windows 修正、通知、托盘和备份。 |

## 你可能想知道

<details>
<summary><b>浏览器能上，WSL、终端或商店应用却不行</b></summary>
<br />

这些程序不读取 Windows 的系统代理。打开 *设置 › Windows 原生*，Clash# 会逐项检查 WSL、终端和 Microsoft Store，每一项都可以单独修正，也可以撤回。
</details>

<details>
<summary><b>启动时提示有冲突</b></summary>
<br />

说明有别的东西正在做 Clash# 打算做的事：另一个 mihomo 内核、占着 10000 端口的程序、开着的手动代理，或者一块 VPN 网卡。对话框会逐条说明，能安全修复的就给出修复按钮。VPN 网卡只会被指出来，Clash# 绝不会替你禁用。随时可以在 *设置 › 启动时* 重新检查。
</details>

<details>
<summary><b>Clash# 崩溃后，系统代理还开着</b></summary>
<br />

照理不会：Clash# 一旦异常退出，看门狗会立刻还原系统代理。如果电脑在那之前就断了电，Clash# 下次启动时会把残留收拾干净。你也可以在 *设置 › 启动时* 注册一个小助手，在登录 Windows 时就先做这件事，不必等 Clash# 打开。
</details>

<details>
<summary><b>有些地区的名称和旗帜跟配置里不一样</b></summary>
<br />

这是默认开启的「中国大陆特色功能」：它按中国大陆的习惯，调整界面上部分地区的名称与旗帜。改的只是显示——配置、日志、搜索、复制的文本和导出的数据都原样不动。不需要的话，在设置里关掉即可。
</details>

<details>
<summary><b>备份与更新</b></summary>
<br />

*设置 › 数据* 可以把设置导出成一个文件，愿意的话连同配置和订阅一起，日后再导入还原。日志可以单独导出为 SQLite 数据库。

*关于* 页面会到 GitHub Releases 检查新版本，更新本身仍由安装器完成。
</details>

## 从源码构建

需要 Windows x64、PowerShell 7，以及 [`global.json`](./global.json) 中固定版本的 .NET SDK。

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

制作安装包还要多几步，见 [Installer/README.md](./ClashSharp/Installer/README.md)。

<details>
<summary><b>仓库结构</b></summary>
<br />

| 路径 | 内容 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 桌面应用 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 领域模型、应用逻辑，以及 Windows 与存储适配 |
| `ClashSharp/ClashSharp.MihomoService` | 为透明代理运行内核的 Windows 服务 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 异常退出后负责还原系统代理 |
| `ClashSharp/ClashSharp.Installer*` | WPF 安装器及其事务逻辑 |
| `ClashSharp/SandboxTest` | 在 Windows 沙盒里做的安装包冒烟测试 |
| `docs/` | 设计记录与开发记录 |

</details>

欢迎参与贡献，动手之前请先读一读 [CodingStyle.md](./CodingStyle.md)。

## 致谢与许可

代理内核是 MetaCubeX 的 [mihomo](https://github.com/MetaCubeX/mihomo)，以 GPL-3.0 发布；GeoIP 与 GeoSite 数据来自 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)。

Clash# 本身以 [AGPL-3.0](./LICENSE) 协议开源。
