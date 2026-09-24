<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**A native Windows proxy client built on the mihomo core**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#installation)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

**English** · [简体中文](./README-zh.md) · [繁體中文](./README-zh-Hant.md) · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 is under development and has no official release. Packages produced by CI are unsigned test builds and are not intended for everyday use.

Clash# is a native Windows proxy client built on the [mihomo](https://github.com/MetaCubeX/mihomo) core. The application is written in C# with .NET 10 and WinUI 3 and is distributed as an MSIX package; a standalone WPF installer handles installation, repair, and removal. Supported systems are Windows 11 x64 and Windows Server 2025 with Desktop Experience.

Beyond managing subscriptions, profiles, nodes, and rules, Clash# covers several Windows-specific concerns: taking over and restoring the system proxy, machine-wide transparent proxy (TUN), proxy access for WSL, terminals, and Microsoft Store apps, and proxy settings left behind after an abnormal exit.

## Features

| Area | Description |
| :--- | :--- |
| **Interface** | Native WinUI 3 controls, Fluent icons, and Windows 11 materials, with light, dark, and system accent color support. Master Control uses a tile layout; tiles can be shown, hidden, and reordered. |
| **Startup checks** | Before proxy mode starts, checks for external mihomo processes, occupied ports, a Windows manual proxy, and other TUN/VPN adapters, and offers fixes for items that can be handled safely. |
| **Proxy restoration** | Restores the system proxy on exit; after an abnormal exit, a recovery process restores it immediately; stale proxy settings are checked and cleared at startup. |
| **Windows network repair** | Diagnoses proxy access for WSL, terminals, and the Microsoft Store separately, with fixes applied or reverted per item. |
| **Triggers** | Runs preset actions in order once all conditions are met. Conditions cover traffic, rate, connection count, runtime, and system time; actions include switching mode, closing connections, sending notifications, and exiting. |
| **Statistics and logs** | Traffic, connection, node health, and rule-hit records are stored in a local SQLite database. |
| **Interface languages** | 简体中文, 繁體中文, English, 한국어, Русский, Français, Deutsch, and فارسی (right-to-left layout). |

## Modes

Clash# names each mode after its effect on the network. The correspondence with common client terminology:

| Clash# | Common name | Behavior |
| :--- | :--- | :--- |
| **Disabled** | Off | Stops the core and restores the system proxy. |
| **Standby** | Direct | The core runs; traffic goes direct by default. |
| **Rule takeover** | Rule | Traffic is routed by the rules in the profile. |
| **Full takeover** | Global | All traffic goes through the selected proxy. |

Transparent proxy (TUN) is controlled by *Transparent proxy* under **Settings › Proxy** and is on by default. When it is on and the Mihomo service is available, takeover runs through TUN and also covers programs that do not read the system proxy. When the Mihomo service is not deployed, takeover runs through the Windows system proxy instead.

> [!CAUTION]
> Transparent proxy takes over routing and DNS for the entire computer. Clash# is designed for one computer with one interactive user and does not isolate traffic between user sessions.

With *Enable tray color status indicator* on under **Settings › Taskbar tray**, the tray icon reflects the current state by color:

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="Gray tray icon" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="Green tray icon" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="Purple tray icon" /> |
| :---: | :---: | :---: |
| Disabled or Standby | System proxy | Transparent proxy |

## Installation

**Requirements:** Windows 11 x64, or Windows Server 2025 with Desktop Experience.

Once an official version is released, packages are published on the [Releases](https://github.com/Water-Run/ClashSharp/releases) page.

1. Download and extract the release package. `ClashSharp-Installer.exe` must stay next to its `payload` folder.
2. Run `ClashSharp-Installer.exe` in the current user session. The application is installed for the user who runs the installer; running as administrator is not required, and .NET does not need to be preinstalled.
3. The installer requests administrator approval when deploying the Mihomo service and any required certificate trust. The displayed publisher should be verified before approval.

Repair, upgrade, and removal are all done by running the same installer again.

> [!WARNING]
> Uninstallation should go through `ClashSharp-Installer.exe`. Removing the app only from Windows *Settings › Apps* leaves machine-level resources such as the Mihomo service in place.

## Getting started

1. Add a subscription link under **Proxies › Links**. Clash# downloads and validates the corresponding profile.
2. Confirm under **Proxies › Profiles** that the profile is the current one.
3. Select **Rule takeover** on **Master Control**.
4. Select a node under **Proxies › Nodes**; a latency test can be run first.

On each launch, Clash# shows a startup guide summarizing the checks for subscription, transparent proxy service, startup restore, and stale proxy settings. The guide can be turned off under **Settings › Startup**.

The local mixed port defaults to `10000` and serves both HTTP and SOCKS. Programs that do not read the system proxy can use `127.0.0.1:10000` directly. The port can be changed under **Settings › Proxy**.

Closing the main window minimizes Clash# to the tray by default, and it keeps running. *Safe exit* in the tray menu is the intended way to quit; on exit, the core stops and the system proxy is restored by default.

### Pages

| Page | Purpose |
| :--- | :--- |
| **Master Control** | Mode switching; tiles show core status, rates, traffic, latency, subscription usage, public IP, and more. |
| **Proxies** | Nodes, profiles (with version history), subscription links, and rules. |
| **Triggers** | Automation tasks, evaluated from top to bottom. |
| **Connections** | Active connections with their process, matched rule, and proxy route. |
| **Statistics** | Long-term totals by profile, node, and time; system logs are also reached from here. |
| **Settings** | Language and appearance, startup, proxy, native Windows repair, notifications, tray, and data backup. |

## FAQ

<details>
<summary><b>WSL, a terminal, or a Microsoft Store app cannot reach the network through the proxy</b></summary>
<br />

These programs do not read the Windows system proxy. **Settings › Native Windows** runs a diagnosis for each of them and applies a fix based on the result; each fix can be reverted at any time. With transparent proxy on, traffic from these programs is taken over as well.
</details>

<details>
<summary><b>A conflict is reported at startup</b></summary>
<br />

The conflict check covers four cases: an external mihomo process, an occupied proxy port, an enabled Windows manual proxy, and other TUN or VPN adapters. The dialog explains each item and offers a fix where one can be applied safely. TUN and VPN adapters are only reported; Clash# does not disable them. The check can be run again at any time under **Settings › Startup**.
</details>

<details>
<summary><b>The system proxy remains on after an abnormal exit</b></summary>
<br />

When Clash# exits abnormally, the recovery process immediately restores the system proxy still owned by Clash#. If the computer shut down before that, Clash# checks for and clears the leftover setting at its next launch. *Startup restore fallback* under **Settings › Startup** can also be registered so that the same cleanup runs at Windows sign-in, without waiting for Clash# to start.
</details>

<details>
<summary><b>Region names or flags differ from the profile</b></summary>
<br />

**Settings › Mainland China features** enables flag replacement and text completion by default, adjusting the names and flags of some regions in the interface according to mainland China conventions. It affects display only; profiles, logs, search, copied text, and exported data are not modified. It can be turned off in the same place.
</details>

<details>
<summary><b>Backup and updates</b></summary>
<br />

**Settings › Data** exports Clash# settings to a single file, optionally including proxy configuration and subscriptions, for later import. Logs can be exported separately as an SQLite database; importing logs is not supported.

The **About** page checks GitHub Releases for new versions. Updates are installed by the installer.
</details>

## Building from source

Building requires Windows x64, PowerShell 7, and the .NET SDK version specified in [`global.json`](./global.json).

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Building the installer package is described in [Installer/README.md](./ClashSharp/Installer/README.md) (Chinese).

<details>
<summary><b>Repository layout</b></summary>
<br />

| Path | Contents |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 application |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | Domain model, application logic, and Windows and storage adapters |
| `ClashSharp/ClashSharp.MihomoService` | Windows service that runs the core for transparent proxy |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | Recovery process that restores the system proxy after an abnormal exit |
| `ClashSharp/ClashSharp.Installer*` | WPF installer and its transaction logic |
| `ClashSharp/SandboxTest` | Package smoke tests in Windows Sandbox |
| `docs/` | Design and development records (Chinese) |

</details>

Coding conventions are described in [CodingStyle.md](./CodingStyle.md).

## License

The proxy core is [mihomo](https://github.com/MetaCubeX/mihomo) by MetaCubeX, released under GPL-3.0. GeoIP and GeoSite data come from [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat).

Clash# is released under the [AGPL-3.0](./LICENSE) license.
