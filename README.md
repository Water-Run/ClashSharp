# `Clash#`

*[中文](./README-zh.md)*

![Clash# Logo](./Logo.png)

`Clash#` is a modern, Windows-native proxy client based on [mihomo](https://github.com/MetaCubeX/mihomo). It targets Windows 11 x64 and is distributed as an MSIX package through the native Clash# installer.

## Installation

Download the release package from [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases), extract it, and run the installer. A release contains `ClashSharp-Installer.exe` and its sibling `payload` directory.

The installer checks Windows 11 x64 compatibility, installs the package certificate when needed, and deploys the MSIX package. If Clash# is already installed, the installer enters maintenance mode for check, repair by redeploying the current payload, or uninstall.

Maintainers can run `Tools\Update-Mihomo.ps1` to fetch or refresh the Windows x64 mihomo core. Release builds download the core when it is missing and probe its version before packaging.

## Windows-Native Features

Clash# uses native WinUI 3 controls, Fluent icons, and Windows 11 acrylic surfaces. The application is designed around Windows networking behavior rather than generic cross-platform proxy terminology.

Windows-focused tools include WSL network repair, terminal proxy diagnostics, Microsoft Store network repair, proxy residue cleanup after abnormal exits, system proxy restoration on exit, and transparent proxy fallback when TUN is unavailable.

## Basic Usage

Use **Master Control** to switch Clash# between disabled, standby, rule takeover, and full takeover modes.

Use **Proxies** to manage nodes, profiles, subscription links, and rules. Use **Statistics** and **Logs** to inspect persistent SQLite-backed traffic records, rule hits, and log storage.

## Advanced Usage

Advanced users can configure transparent proxy mode, background connection sampling, profile import and validation, node latency testing, Windows-native repair actions, SQLite log cleanup, and mainland China display behavior.

Mainland China display is enabled by default. It changes regional display text and flag presentation at the UI layer without modifying profiles, logs, search, copy, or export data.

`Clash#` is open-source under the `AGPL 3.0` license.
