<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**採用 mihomo 核心的 Windows 原生代理用戶端**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#安裝)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · [简体中文](./README-zh.md) · **繁體中文** · [한국어](./README-ko.md) · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0 仍在開發中，目前沒有正式發行版本。CI 產生的安裝套件為未簽署的測試組建，不適用於日常使用。

Clash# 是採用 [mihomo](https://github.com/MetaCubeX/mihomo) 核心的 Windows 原生代理用戶端。主程式以 C#、.NET 10 與 WinUI 3 開發，以 MSIX 套件發行，由獨立的 WPF 安裝程式負責安裝、修復與解除安裝。適用於 Windows 11 x64，以及具備桌面體驗的 Windows Server 2025。

除訂閱、設定檔、節點與規則的管理外，Clash# 也處理 Windows 平台上的幾類具體問題：系統代理的接管與還原，整機透明代理（TUN），WSL、終端機與 Microsoft Store 應用程式的代理存取，以及程式異常結束後遺留的代理設定。

## 功能

| 面向 | 說明 |
| :--- | :--- |
| **介面** | WinUI 3 原生控制項、Fluent 圖示與 Windows 11 材質，支援淺色、深色與系統輔色。主控頁採用磁貼版面，磁貼可顯示、隱藏與排序。 |
| **啟動檢查** | 啟動代理前檢查外部 mihomo 處理程序、連接埠占用、Windows 手動代理及其他 TUN/VPN 網路卡，並對可安全處理的項目提供修復。 |
| **代理還原** | 結束時還原系統代理；異常結束時由復原處理程序立即還原；啟動時檢查並清除殘留的代理設定。 |
| **Windows 網路修正** | 分別診斷 WSL、終端機與 Microsoft Store 的代理存取，逐項套用或復原修正。 |
| **觸發器** | 條件全部符合時，依序執行預設動作。條件包括流量、速率、連線數、執行時間與系統時間；動作包括切換模式、中斷連線、傳送通知與結束程式。 |
| **統計與日誌** | 流量、連線、節點健康與規則命中紀錄儲存於本機 SQLite 資料庫。 |
| **介面語言** | 简体中文、繁體中文、English、한국어、Русский、Français、Deutsch、فارسی（由右至左版面）。 |

## 模式

Clash# 依模式對網路的作用為其命名。與常見用戶端的對應關係如下：

| Clash# | 常見名稱 | 行為 |
| :--- | :--- | :--- |
| **未啟用** | 關閉 | 停止核心，還原系統代理。 |
| **待命** | 直連 | 核心執行，流量預設直連。 |
| **依規則接管** | 規則 | 依設定檔中的規則分流。 |
| **接管全部** | 全域 | 全部流量經由所選代理。 |

透明代理（TUN）由 **設定 › 代理** 中的「透明代理」控制，預設開啟。開啟且 Mihomo 服務可用時，接管透過 TUN 完成，不讀取系統代理的程式同樣受其涵蓋；Mihomo 服務未部署時，接管改由 Windows 系統代理完成。

> [!CAUTION]
> 透明代理接管整台電腦的路由與 DNS。Clash# 依「一台電腦、一位互動使用者」設計，不提供多使用者工作階段之間的流量隔離。

啟用 **設定 › 工作列托盤** 中的「啟用托盤色彩狀態指示」後，托盤圖示以顏色區分目前狀態：

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="灰色托盤圖示" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="綠色托盤圖示" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="紫色托盤圖示" /> |
| :---: | :---: | :---: |
| 未啟用或待命 | 系統代理 | 透明代理 |

## 安裝

**系統需求**：Windows 11 x64，或具備桌面體驗的 Windows Server 2025。

正式版本發行後，安裝套件提供於 [Releases](https://github.com/Water-Run/ClashSharp/releases) 頁面。

1. 下載並解壓縮發行套件。`ClashSharp-Installer.exe` 須與同目錄的 `payload` 資料夾保持在一起。
2. 於目前的使用者工作階段執行 `ClashSharp-Installer.exe`。應用程式安裝於執行安裝程式的使用者名下，無需以系統管理員身分執行，也無需預先安裝 .NET。
3. 部署 Mihomo 服務及必要的憑證信任時，安裝程式要求系統管理員授權。授權前應核對所顯示的發行者。

修復、升級與解除安裝均透過再次執行同一安裝程式完成。

> [!WARNING]
> 解除安裝應透過 `ClashSharp-Installer.exe` 進行。僅在 Windows 的「設定 › 應用程式」中移除時，Mihomo 服務等電腦層級資源不會同步清除。

## 快速上手

1. 在 **代理 › 連結** 中加入訂閱連結。Clash# 下載並驗證對應的設定檔。
2. 在 **代理 › 設定檔** 中確認該設定檔為目前設定檔。
3. 在 **主控** 中選擇 **依規則接管**。
4. 在 **代理 › 節點** 中選擇節點，可先進行延遲測試。

Clash# 每次啟動時顯示啟動導引，彙整訂閱、透明代理服務、啟動還原與代理殘留的檢查結果。此導引可在 **設定 › 啟動時** 中關閉。

本機混合連接埠預設為 `10000`，HTTP 與 SOCKS 共用。不讀取系統代理的程式可直接使用 `127.0.0.1:10000`。連接埠可在 **設定 › 代理** 中修改。

關閉主視窗時，Clash# 預設最小化至托盤並繼續執行。結束程式應使用托盤選單中的「安全結束」；結束時預設停止核心並還原系統代理。

### 頁面

| 頁面 | 用途 |
| :--- | :--- |
| **主控** | 切換模式；以磁貼顯示核心狀態、速率、流量、延遲、訂閱用量、公用 IP 等資訊。 |
| **代理** | 節點、設定檔（含歷史版本）、訂閱連結與規則。 |
| **觸發器** | 自動化工作，由上而下依序評估。 |
| **連線** | 使用中的連線及其所屬處理程序、命中規則與代理路徑。 |
| **統計資料** | 依設定檔、節點與時間彙整的長期統計；系統日誌亦由此進入。 |
| **設定** | 語言與外觀、啟動、代理、Windows 原生修正、通知、托盤與資料備份。 |

## 常見問題

<details>
<summary><b>WSL、終端機或 Microsoft Store 應用程式無法透過代理存取網路</b></summary>
<br />

上述程式不讀取 Windows 系統代理。**設定 › Windows 原生** 可對相應項目執行診斷，並依結果套用修正；修正可隨時復原。開啟透明代理後，此類程式的流量同樣會被接管。
</details>

<details>
<summary><b>啟動時提示衝突</b></summary>
<br />

衝突檢查涵蓋四類情況：外部 mihomo 處理程序、代理連接埠遭占用、Windows 手動代理已開啟、存在其他 TUN 或 VPN 網路卡。對話方塊逐項說明，並對可安全處理的項目提供修復動作。TUN 與 VPN 網路卡僅作提示，Clash# 不會自動停用。檢查可在 **設定 › 啟動時** 中隨時重新執行。
</details>

<details>
<summary><b>異常結束後，系統代理仍處於開啟狀態</b></summary>
<br />

Clash# 異常結束時，復原處理程序會立即還原仍歸 Clash# 所有的系統代理。若電腦在此之前已關機，Clash# 將於下次啟動時檢查並清除殘留。另可在 **設定 › 啟動時** 中註冊「啟動還原輔助服務」，使清除在登入 Windows 時即執行，而無需等待 Clash# 啟動。
</details>

<details>
<summary><b>地區名稱或旗幟與設定檔不一致</b></summary>
<br />

**設定 › 中國大陸特色功能** 預設啟用旗幟替換與文字補全，依中國大陸的規範調整介面中部分地區的名稱與旗幟。此功能僅作用於介面顯示，不修改設定檔、日誌、搜尋、複製內容與匯出資料，可在同一位置關閉。
</details>

<details>
<summary><b>備份與更新</b></summary>
<br />

**設定 › 資料** 可將 Clash# 設定匯出為單一檔案，並可選擇一併包含代理設定與訂閱，供日後匯入還原。日誌可單獨匯出為 SQLite 資料庫，不支援匯入。

**關於** 頁面檢查 GitHub Releases 上的新版本。更新由安裝程式完成。
</details>

## 從原始碼建置

需要 Windows x64、PowerShell 7，以及 [`global.json`](./global.json) 中指定版本的 .NET SDK。

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

安裝套件的建置步驟見 [Installer/README.md](./ClashSharp/Installer/README.md)（簡體中文）。

<details>
<summary><b>儲存庫結構</b></summary>
<br />

| 路徑 | 內容 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 主程式 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 領域模型、應用邏輯，以及 Windows 與儲存配接 |
| `ClashSharp/ClashSharp.MihomoService` | 為透明代理執行核心的 Windows 服務 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 異常結束後還原系統代理的復原處理程序 |
| `ClashSharp/ClashSharp.Installer*` | WPF 安裝程式及其交易邏輯 |
| `ClashSharp/SandboxTest` | 以 Windows 沙箱進行的安裝套件冒煙測試 |
| `docs/` | 設計紀錄與開發紀錄（簡體中文） |

</details>

編碼慣例見 [CodingStyle.md](./CodingStyle.md)。

## 授權

代理核心為 MetaCubeX 的 [mihomo](https://github.com/MetaCubeX/mihomo)，以 GPL-3.0 授權發行；GeoIP 與 GeoSite 資料來自 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)。

Clash# 以 [AGPL-3.0](./LICENSE) 授權開源。
