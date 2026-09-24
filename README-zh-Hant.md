<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**為 Windows 而生的代理用戶端，採用 [mihomo](https://github.com/MetaCubeX/mihomo) 核心。**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#安裝)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · [简体中文](./README-zh.md) · **繁體中文** · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 仍在開發中，尚未推出正式版本。
> CI 產生的安裝套件是未簽署的測試組建，不適合日常使用。

多數代理用戶端追求到處都能執行；Clash# 只打算在一個系統上執行，而且要執行得好。

它以 C# 與 WinUI 3 寫成，看起來就是 Windows 11 的一部分，而不像借住的客人。它也熟悉 Windows 使用者遲早會碰上的那些麻煩：不理會系統代理的程式、自成一片網路的 WSL、連不上 `localhost` 的市集應用程式，還有用戶端當掉之後依然開著的系統代理。

你只需要一個 Clash 訂閱，其餘交給 Clash#。

## 特色

- **像 Windows 自己的應用程式。** 原生控制項、Fluent 圖示、半透明材質，淺色深色隨你，輔色也跟著你走。首頁是一面仿照「快速設定」的磁貼牆：留下你在意的，拖到你順手的位置。
- **來時什麼樣，走時還什麼樣。** 啟動之前，Clash# 會先看看有沒有擋路的：另一個 mihomo、被占用的連接埠、殘留的手動代理、其他 VPN 網路卡。萬一它意外結束，一個小小的看門狗會立刻關掉它留下的系統代理。
- **補上 Windows 的老缺口。** 按一下，WSL、終端機和 Microsoft Store 應用程式也能走代理。
- **該自動的時候就自動。** 觸發器是一條條「當……就……」的小規則。例如：*本次執行流量超過 5 GB，就切到待命，再通知我一聲。*
- **記得住。** 流量、連線與節點健康狀況都存在本機的 SQLite 裡，統計頁看到的不只是此時此刻。
- **說你的語言。** 简体中文、繁體中文、English、한국어、Русский、Français、Deutsch，以及由右至左排版的 فارسی。

## 模式

Clash# 依「對網路做了什麼」來替模式命名。如果你用過其他 Clash 用戶端，可以這樣對照：

| Clash# 裡叫 | 一般叫 | 會發生什麼 |
| :--- | :--- | :--- |
| **未啟用** | 關閉 | 核心停止，系統代理還原。 |
| **待命** | 直連 | 核心在執行，但流量直接出門。 |
| **依規則接管** | 規則 | 流量依設定檔中的規則分流。 |
| **接管全部** | 全域 | 所有流量都經由你選定的代理。 |

**透明代理**（即 TUN 模式）是 *設定 › 代理* 裡的獨立開關。它能接住每個程式的流量，連那些對系統代理視而不見的也不例外。萬一它沒能啟動，Clash# 可以自動退回系統代理。

> [!CAUTION]
> 透明代理會接管整台電腦的路由與 DNS。Clash# 依「一台電腦、一位登入使用者」設計，不會將不同使用者的流量彼此隔開。

在設定中開啟「托盤色彩狀態指示」，托盤圖示一眼就能告訴你目前的狀態：

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="灰色托盤圖示" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="綠色托盤圖示" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="紫色托盤圖示" /> |
| :---: | :---: | :---: |
| 未啟用或待命 | 系統代理 | 透明代理 |

## 安裝

**系統需求：** Windows 11 x64，或具備桌面體驗的 Windows Server 2025。

1.0.0 正式推出後，安裝套件會出現在 [Releases](https://github.com/Water-Run/ClashSharp/releases) 頁面。

1. 下載並解壓縮。`ClashSharp-Installer.exe` 與 `payload` 資料夾要放在一起。
2. 以你自己的帳戶按兩下 `ClashSharp-Installer.exe`。Clash# 會安裝給執行安裝程式的那個人，因此不必「以系統管理員身分執行」，也不必事先安裝 .NET。
3. Windows 會詢問一次權限，用來安裝透明代理所需的背景服務。確認發行者是你認得的那一位，再按允許。

日後想修復、更新或解除安裝，再執行一次同一個安裝程式即可。

> [!WARNING]
> 請用 `ClashSharp-Installer.exe` 解除安裝，而不是在 *設定 › 應用程式* 中移除。只從 Windows 刪掉應用程式，背景服務會被遺留下來。

## 上手

1. 在 **代理 › 連結** 加入訂閱連結，Clash# 會取回它背後的設定檔。
2. 在 **代理 › 設定檔** 確認選取的正是這份設定檔。
3. 回到 **主控**，選擇 **依規則接管**。現在你已經透過代理上網了。
4. 在 **代理 › 節點** 挑一個節點，或測一測延遲，找個快的。

每次啟動，Clash# 都會跳出一張簡短的檢查清單，確認一切就緒。等你不再需要它，可以在 *設定 › 啟動時* 關閉。

不理會系統代理的程式，可以直接指向 `127.0.0.1:10000`——HTTP 與 SOCKS 共用這一個連接埠。連接埠可在 *設定 › 代理* 中修改。

### 各個頁面

| 頁面 | 用來做什麼 |
| :--- | :--- |
| **主控** | 切換模式；磁貼顯示網速、流量、延遲、訂閱用量、公用 IP 等等。 |
| **代理** | 節點、設定檔（每份都有歷史版本）、訂閱連結與規則。 |
| **觸發器** | 你的自動化規則，由上而下依序判斷。 |
| **連線** | 每一條使用中的連線，連同它的處理程序、命中的規則與經過的代理路徑。 |
| **統計資料** | 依設定檔、依節點、依時間累積的長期統計。 |
| **設定** | 語言與外觀、啟動、透明代理、Windows 修正、通知、托盤與備份。 |

## 你可能想知道

<details>
<summary><b>瀏覽器能上網，WSL、終端機或市集應用程式卻不行</b></summary>
<br />

這些程式不會讀取 Windows 的系統代理。開啟 *設定 › Windows 原生*，Clash# 會逐項檢查 WSL、終端機與 Microsoft Store，每一項都能個別修正，也能復原。
</details>

<details>
<summary><b>啟動時提示有衝突</b></summary>
<br />

表示有別的東西正在做 Clash# 打算做的事：另一個 mihomo 核心、占用 10000 連接埠的程式、開著的手動代理，或是一張 VPN 網路卡。對話方塊會逐條說明，能安全修復的就提供修復按鈕。VPN 網路卡只會被指出來，Clash# 絕不會替你停用。隨時可以在 *設定 › 啟動時* 重新檢查。
</details>

<details>
<summary><b>Clash# 當掉後，系統代理還開著</b></summary>
<br />

照理不會：Clash# 一旦異常結束，看門狗會立刻還原系統代理。如果電腦在那之前就斷了電，Clash# 下次啟動時會把殘留收拾乾淨。你也可以在 *設定 › 啟動時* 註冊一個小幫手，在登入 Windows 時就先處理，不必等 Clash# 開啟。
</details>

<details>
<summary><b>有些地區的名稱與旗幟跟設定檔裡不一樣</b></summary>
<br />

這是預設開啟的「中國大陸特色功能」：它依中國大陸的慣例，調整介面上部分地區的名稱與旗幟。改的只是顯示——設定檔、日誌、搜尋、複製的文字與匯出的資料都原封不動。不需要的話，在設定中關閉即可。
</details>

<details>
<summary><b>備份與更新</b></summary>
<br />

*設定 › 資料* 可以把設定匯出成一個檔案，願意的話連同設定檔與訂閱一起，日後再匯入還原。日誌可以單獨匯出為 SQLite 資料庫。

*關於* 頁面會到 GitHub Releases 檢查新版本，更新本身仍由安裝程式完成。
</details>

## 從原始碼建置

需要 Windows x64、PowerShell 7，以及 [`global.json`](./global.json) 中固定版本的 .NET SDK。

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

製作安裝套件還要多幾個步驟，請見 [Installer/README.md](./ClashSharp/Installer/README.md)（簡體中文）。

<details>
<summary><b>儲存庫結構</b></summary>
<br />

| 路徑 | 內容 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 桌面應用程式 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 領域模型、應用邏輯，以及 Windows 與儲存配接 |
| `ClashSharp/ClashSharp.MihomoService` | 為透明代理執行核心的 Windows 服務 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 異常結束後負責還原系統代理 |
| `ClashSharp/ClashSharp.Installer*` | WPF 安裝程式及其交易邏輯 |
| `ClashSharp/SandboxTest` | 在 Windows 沙箱中進行的安裝套件冒煙測試 |
| `docs/` | 設計紀錄與開發紀錄（簡體中文） |

</details>

歡迎參與貢獻，動手之前請先讀一讀 [CodingStyle.md](./CodingStyle.md)。

## 致謝與授權

代理核心是 MetaCubeX 的 [mihomo](https://github.com/MetaCubeX/mihomo)，以 GPL-3.0 授權發布；GeoIP 與 GeoSite 資料來自 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)。

Clash# 本身以 [AGPL-3.0](./LICENSE) 授權開源。
