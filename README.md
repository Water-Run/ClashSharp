<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**A proxy client made for Windows, powered by the [mihomo](https://github.com/MetaCubeX/mihomo) core.**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#install)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

**English** · [简体中文](./README-zh.md) · [繁體中文](./README-zh-Hant.md) · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 is still being built, and there is no official release yet.
> The packages CI produces are unsigned test builds, not something to depend on every day.

Most proxy clients are made to run everywhere. Clash# is made to run on one system, and to run there well.

It is written in C# and WinUI 3, and it feels like part of Windows 11 rather than a guest in it. It also knows the things a Windows user sooner or later trips over: apps that ignore the system proxy, WSL living on its own network, Store apps that cannot reach `localhost`, and the proxy setting still switched on after a client has crashed.

Bring a Clash subscription. Clash# takes care of the rest.

## Highlights

- **At home on Windows 11.** Native controls, Fluent icons, translucent materials, light and dark themes, your own accent color. The home page is a grid of tiles in the spirit of Quick Settings: keep the ones you care about and drag them into place.
- **Leaves your network as it found it.** Before it starts, Clash# looks for anything in the way: another mihomo, a busy port, a leftover manual proxy, a VPN adapter. If it ever exits unexpectedly, a small watchdog switches off the system proxy it left behind.
- **Closes the usual Windows gaps.** A click lets WSL, terminals, and Microsoft Store apps use the proxy too.
- **Works on its own when you want it to.** Triggers are small "when this, then that" rules. *When this session passes 5 GB, switch to Standby and let me know.*
- **Remembers.** Traffic, connections, and node health are kept locally in SQLite, so Statistics can show you more than the present moment.
- **Speaks your language.** Simplified Chinese, Traditional Chinese, English, Korean, Russian, French, German, and Persian, laid out right to left.

## Modes

Clash# names its modes after what they do to your network. If you are coming from another Clash client, here is the translation:

| In Clash# | Usually called | What happens |
| :--- | :--- | :--- |
| **Disabled** | Off | The core stops and the system proxy is put back. |
| **Standby** | Direct | The core runs, but traffic goes straight out. |
| **Rule takeover** | Rule | Traffic follows the rules in your profile. |
| **Full takeover** | Global | Everything goes through the proxy you selected. |

**Transparent proxy** (TUN mode) is a separate switch in *Settings › Proxy*. It catches traffic from every app, including the ones that pay no attention to the system proxy. Should it fail to start, Clash# can fall back to the system proxy on its own.

> [!CAUTION]
> Transparent proxy takes over routing and DNS for the entire machine. Clash# is designed for one signed-in user per computer and does not keep different users' traffic apart.

Turn on the tray color indicator in Settings, and the icon tells you the state at a glance:

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="Gray tray icon" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="Green tray icon" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="Purple tray icon" /> |
| :---: | :---: | :---: |
| Disabled or Standby | System proxy | Transparent proxy |

## Install

**You need** Windows 11 x64, or Windows Server 2025 with the Desktop Experience.

Once 1.0.0 is out, packages will be waiting on the [Releases](https://github.com/Water-Run/ClashSharp/releases) page.

1. Download and extract the package. Keep `ClashSharp-Installer.exe` and the `payload` folder side by side.
2. Double-click `ClashSharp-Installer.exe` from your own account. Clash# is installed for whoever runs it, so there is no need for *Run as administrator*, and no need to install .NET first.
3. Windows will ask for permission once, to set up the background service behind transparent proxy. Check that the publisher is who you expect, then allow it.

Later on, running the same installer again lets you repair, update, or uninstall.

> [!WARNING]
> Uninstall with `ClashSharp-Installer.exe`, not from *Settings › Apps*. Removing the app through Windows alone leaves its background service behind.

## Getting started

1. In **Proxies › Links**, add your subscription link. Clash# fetches the profile behind it.
2. In **Proxies › Profiles**, make sure that profile is the one selected.
3. On **Master Control**, choose **Rule takeover**. You are now online through the proxy.
4. In **Proxies › Nodes**, pick a node, or run a latency test to find a quick one.

Each time it starts, Clash# shows a short checklist to confirm everything is in order. Once you no longer need it, it can be turned off under *Settings › Startup*.

Apps that ignore the system proxy can be pointed at `127.0.0.1:10000`, where HTTP and SOCKS share the same port. The port can be changed in *Settings › Proxy*.

### Around the app

| Page | What it is for |
| :--- | :--- |
| **Master Control** | Switching modes, plus tiles for speed, traffic, latency, subscription usage, public IP, and more. |
| **Proxies** | Nodes, profiles (each with its own version history), subscription links, and rules. |
| **Triggers** | Your automations, checked from top to bottom. |
| **Connections** | Every live connection, with its process, the rule it matched, and the route it took. |
| **Statistics** | Long-term totals by profile, by node, and over time. |
| **Settings** | Language and theme, startup, transparent proxy, Windows fixes, notifications, tray, and backups. |

## Good to know

<details>
<summary><b>The browser works, but WSL, a terminal, or a Store app does not</b></summary>
<br />

These apps don't read the Windows system proxy. Open *Settings › Native Windows*: Clash# checks WSL, terminals, and the Microsoft Store one at a time, and can apply a fix to each, or take it back.
</details>

<details>
<summary><b>Clash# reports a conflict at startup</b></summary>
<br />

Something else is already doing what Clash# is about to do: another mihomo core, a program holding port 10000, a manual proxy, or a VPN adapter. The dialog explains each one and offers a fix wherever that is safe. VPN adapters are only pointed out; Clash# will never disable them for you. You can run the check again at any time from *Settings › Startup*.
</details>

<details>
<summary><b>The proxy stayed on after Clash# crashed</b></summary>
<br />

It shouldn't: the watchdog restores the system proxy the moment Clash# exits abnormally. If the computer went down before it had the chance, Clash# tidies up the next time it starts. You can also register a small helper under *Settings › Startup* that does the same at sign-in, even before Clash# opens.
</details>

<details>
<summary><b>Some region names and flags differ from my profile</b></summary>
<br />

The *Mainland China features* setting is on by default. It renames and re-flags certain regions in the interface according to mainland China conventions. Only what you see is affected; your profiles, logs, searches, copied text, and exports stay exactly as they were. Switch it off in Settings if you prefer.
</details>

<details>
<summary><b>Backups and updates</b></summary>
<br />

*Settings › Data* exports your settings to a single file, with profiles and subscriptions included if you like, ready to be imported later. Logs can be exported on their own as an SQLite database.

The *About* page checks GitHub Releases for new versions. Updates themselves are installed with the installer.
</details>

## Building from source

You will need Windows x64, PowerShell 7, and the .NET SDK version pinned in [`global.json`](./global.json).

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

Building the installer package takes a few more steps, covered in [Installer/README.md](./ClashSharp/Installer/README.md) (in Chinese).

<details>
<summary><b>Repository layout</b></summary>
<br />

| Path | Contents |
| :--- | :--- |
| `ClashSharp/ClashSharp` | The WinUI 3 desktop app |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | Domain model, application logic, and Windows and storage adapters |
| `ClashSharp/ClashSharp.MihomoService` | The Windows service that runs the core for transparent proxy |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | Puts the system proxy back after an abnormal exit |
| `ClashSharp/ClashSharp.Installer*` | The WPF installer and its transaction logic |
| `ClashSharp/SandboxTest` | Package smoke tests in Windows Sandbox |
| `docs/` | Design notes and development records (in Chinese) |

</details>

Contributions are welcome. Please have a look at [CodingStyle.md](./CodingStyle.md) first.

## Credits and license

The proxy core is [mihomo](https://github.com/MetaCubeX/mihomo) by MetaCubeX, distributed under GPL-3.0. GeoIP and GeoSite data come from [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat).

Clash# itself is released under the [AGPL-3.0](./LICENSE) license.
