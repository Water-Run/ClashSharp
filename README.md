# `Clash#`

*[中文](./README-zh.md)*

![Clash# Logo](./ClashSharp/ClashSharp/Assets/Logo.svg)

`Clash#` is a modern, Windows-native proxy client based on [mihomo](https://github.com/MetaCubeX/mihomo). It targets Windows 11 x64 and is distributed as an MSIX package through the native Clash# installer.

> Development status (2026-09-08): Version 1.0.0 is in development. WinUI pages and tiles, shared backup/restore, connection filtering, bounded log persistence, and settings validation have automated coverage; the current CI passed all 4433 tests. An existing Clash configuration passed 32/32 isolated node probes and HTTPS forwarding. The production installer engine completed installation, repair, uninstall, eight interruption/file-lock recovery cases, a held-service-handle recovery case, and recovery across a Windows Sandbox restart. The installed service also passed real core reload, bounded crash recovery, and active-core uninstall checks. The latest development installer passed its payload audit without a global .NET runtime. Formal signed builds enable the production runtime; development packages remain gated. WPF interaction and the complete release matrix are still pending. Follow the [1.0.0 execution ledger](./docs/reviews/1.0.0-execution-ledger.md) for the exact candidates, evidence, and remaining work.

## Installation

After a formal version is published, download the package from [GitHub Releases](https://github.com/Water-Run/ClashSharp/releases), extract it, and run the Authenticode-signed `ClashSharp-Installer.exe` from your normal user session. The application package belongs to that user; the installer requests UAC for the machine service and any required machine certificate trust. Check the verified publisher before accepting. A release contains the installer and its sibling `payload` directory. The installer is a self-contained WPF executable and needs no preinstalled .NET. Current CI development packages are validation artifacts, not formal releases.

The installer checks Windows 11 x64 compatibility, installs the package certificate when needed, and deploys the MSIX package. Verified certificate/MSIX files and their directory chain stay read-only locked through each consumer, with identity and SHA-256 checks immediately before and after use. After deployment, the installer verifies every package-authored file against the signed block-map payload, and the MSIX enables Windows package-integrity enforcement. If Clash# is already installed, the installer enters maintenance mode for check, in-place update/repair, or uninstall. Use this Installer for a complete uninstall; removing only the MSIX from Windows Settings can leave the machine-level service resources behind.

Release dependency resolution and payload assembly are offline: every .NET project uses the prior locked restore. The build fails closed unless a checked-in Mihomo version/length/SHA-256 manifest matches the bundled ordinary binary and all four pinned GeoData assets have been prepared with `Tools\Prepare-GeoData.ps1`; `Tools\Update-Mihomo.ps1` is an explicit maintainer utility, never an implicit release-build download. Every run uses a new random staging root, admits only the one manifest-declared x64 Windows App Runtime dependency, and requires its approved signer thumbprint in `CLASHSHARP_WINDOWS_APP_RUNTIME_SIGNER_THUMBPRINT`. Official packaging also requires controlled MSIX signing material, a trusted timestamped Authenticode certificate, and an explicit `CLASHSHARP_WINDOWS_SDK_VERSION`; SignTool is accepted only from that Microsoft-signed Windows Kits x64 directory, and signing contacts only the explicitly configured HTTPS timestamp endpoint. The WPF Installer is published as one self-contained executable in disposable staging and promoted to `artifacts\installer\release` only after the exact file set, length, and SHA-256 contract is rechecked. `build.ps1 -Development` produces an explicitly named, non-publishable unsigned artifact.

## Windows-Native Features

Clash# uses native WinUI 3 controls, Fluent icons, and Windows 11 acrylic surfaces. The application is designed around Windows networking behavior rather than generic cross-platform proxy terminology.

Windows-focused tools include WSL network repair, terminal proxy diagnostics, Microsoft Store network repair, proxy residue cleanup after abnormal exits, system proxy restoration on exit, fail-closed transparent proxy activation through TUN, and a tile-based master control surface for status and common actions.

TUN takeover is machine-wide. Clash# currently supports one interactive user and one Core owner per machine; it does not provide multi-session traffic isolation. Once installer execution is released, ownership reassociation must be started by the target user and explicitly confirmed in Repair; ordinary Repair never changes ownership implicitly.

## Basic Usage

Use **Master Control** to switch Clash# between disabled, standby, rule takeover, and full takeover modes.

Use **Proxies** to manage nodes, profiles, subscription links, and rules. Use **Statistics** and **Logs** to inspect persistent SQLite-backed traffic records, rule hits, and log storage.

## Advanced Usage

Advanced users can configure transparent proxy mode, background connection sampling, profile import and validation, node latency testing, Windows-native repair actions, SQLite log cleanup, and mainland China display behavior.

Mainland China display is enabled by default. It changes regional display text and flag presentation at the UI layer without modifying profiles, logs, search, copy, or export data.

`Clash#` is open-source under the `AGPL 3.0` license.
