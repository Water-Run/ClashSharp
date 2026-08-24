# `Clash#`

*[中文](./README-zh.md)*

![Clash# Logo](./Logo.png)

`Clash#` is a modern, Windows-native proxy client based on [mihomo](https://github.com/MetaCubeX/mihomo). It targets Windows 11 x64 and is distributed as an MSIX package through the native Clash# installer.

## Installation

Download the release package from [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases), extract it, and run the Authenticode-signed `ClashSharp-Installer.exe` normally—not with “Run as administrator.” The certificate and MSIX stay in the current-user context; the installer requests UAC separately only for machine-service work. Confirm that UAC shows the expected verified publisher, not “Unknown publisher.” A release contains the installer and its sibling `payload` directory.

The installer checks Windows 11 x64 compatibility, installs the package certificate when needed, and deploys the MSIX package. If Clash# is already installed, the installer enters maintenance mode for check, in-place update/repair, or uninstall. Use this Installer for a complete uninstall; removing only the MSIX from Windows Settings can leave the machine-level service resources behind.

Release dependency resolution and payload assembly are offline: `dotnet publish` uses the prior locked restore, and Cargo uses its frozen lock/cache. The build fails closed unless a checked-in Mihomo version/length/SHA-256 manifest matches the bundled ordinary binary and all four pinned GeoData assets have been prepared with `Tools\Prepare-GeoData.ps1`; `Tools\Update-Mihomo.ps1` is an explicit maintainer utility, never an implicit release-build download. Every run uses a new random staging root, admits only the one manifest-declared x64 Windows App Runtime dependency, and requires its approved signer thumbprint in `CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT`. Official packaging also requires controlled MSIX signing material, a trusted timestamped Authenticode certificate, and an explicit `CLASHSHARP_WINDOWS_SDK_VERSION`; SignTool is accepted only from that Microsoft-signed Windows Kits x64 directory, and signing contacts only the explicitly configured HTTPS timestamp endpoint. Unsigned Cargo output stays in disposable staging and is promoted to `target\release-artifacts` only after the exact file set, length, and SHA-256 contract is rechecked. `build.ps1 -Development` produces an explicitly named, non-publishable unsigned artifact.

## Windows-Native Features

Clash# uses native WinUI 3 controls, Fluent icons, and Windows 11 acrylic surfaces. The application is designed around Windows networking behavior rather than generic cross-platform proxy terminology.

Windows-focused tools include WSL network repair, terminal proxy diagnostics, Microsoft Store network repair, proxy residue cleanup after abnormal exits, system proxy restoration on exit, fail-closed transparent proxy activation through TUN, and a tile-based master control surface for status and common actions.

TUN takeover is machine-wide. Clash# currently supports one interactive user and one Core owner per machine; it does not provide multi-session traffic isolation. To re-associate ownership, run Repair from the ClashSharp Installer as the target user.

## Basic Usage

Use **Master Control** to switch Clash# between disabled, standby, rule takeover, and full takeover modes.

Use **Proxies** to manage nodes, profiles, subscription links, and rules. Use **Statistics** and **Logs** to inspect persistent SQLite-backed traffic records, rule hits, and log storage.

## Advanced Usage

Advanced users can configure transparent proxy mode, background connection sampling, profile import and validation, node latency testing, Windows-native repair actions, SQLite log cleanup, and mainland China display behavior.

Mainland China display is enabled by default. It changes regional display text and flag presentation at the UI layer without modifying profiles, logs, search, copy, or export data.

`Clash#` is open-source under the `AGPL 3.0` license.
