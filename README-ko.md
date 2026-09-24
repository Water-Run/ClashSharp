<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**mihomo 코어 기반의 Windows 네이티브 프록시 클라이언트**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#설치)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · [简体中文](./README-zh.md) · [繁體中文](./README-zh-Hant.md) · **한국어** · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0은 개발 중이며, 정식 릴리스는 아직 없다. CI가 생성하는 설치 패키지는 서명되지 않은 테스트 빌드로, 일상적인 사용을 위한 것이 아니다.

Clash#는 [mihomo](https://github.com/MetaCubeX/mihomo) 코어 기반의 Windows 네이티브 프록시 클라이언트이다. 본 프로그램은 C#, .NET 10, WinUI 3로 개발되어 MSIX 패키지로 배포되며, 설치·복구·제거는 별도의 WPF 설치 프로그램이 담당한다. 지원 대상은 Windows 11 x64와 데스크톱 환경을 포함한 Windows Server 2025이다.

구독, 프로필, 노드, 규칙 관리 외에도 Clash#는 Windows 환경의 몇 가지 구체적인 문제를 다룬다. 시스템 프록시의 인계와 복원, 컴퓨터 전체에 적용되는 투명 프록시(TUN), WSL·터미널·Microsoft Store 앱의 프록시 접근, 그리고 비정상 종료 후 남는 프록시 설정이 그 대상이다.

## 기능

| 영역 | 설명 |
| :--- | :--- |
| **인터페이스** | WinUI 3 네이티브 컨트롤, Fluent 아이콘, Windows 11 소재를 사용하며 밝은 테마, 어두운 테마, 시스템 강조 색을 지원한다. 메인 컨트롤은 타일 배치를 사용하며, 타일은 표시·숨김·순서 변경이 가능하다. |
| **시작 점검** | 프록시 시작 전 외부 mihomo 프로세스, 포트 사용, Windows 수동 프록시, 다른 TUN/VPN 어댑터를 점검하고, 안전하게 처리할 수 있는 항목에는 수정을 제공한다. |
| **프록시 복원** | 종료 시 시스템 프록시를 복원한다. 비정상 종료 시에는 복구 프로세스가 즉시 복원하며, 시작 시 남은 프록시 설정을 점검하고 정리한다. |
| **Windows 네트워크 보정** | WSL, 터미널, Microsoft Store의 프록시 접근을 각각 진단하고, 항목별로 보정을 적용하거나 되돌린다. |
| **트리거** | 모든 조건이 충족되면 미리 정한 동작을 순서대로 실행한다. 조건에는 트래픽, 속도, 연결 수, 실행 시간, 시스템 시각이 있고, 동작에는 모드 전환, 연결 끊기, 알림 보내기, 프로그램 종료가 있다. |
| **통계와 로그** | 트래픽, 연결, 노드 상태, 규칙 일치 기록은 로컬 SQLite 데이터베이스에 저장된다. |
| **인터페이스 언어** | 简体中文, 繁體中文, English, 한국어, Русский, Français, Deutsch, فارسی(오른쪽에서 왼쪽 배치). |

## 모드

Clash#는 각 모드를 네트워크에 미치는 작용에 따라 명명한다. 일반적인 클라이언트 용어와의 대응은 다음과 같다.

| Clash# | 일반적인 이름 | 동작 |
| :--- | :--- | :--- |
| **사용 안 함** | 끄기 | 코어를 중지하고 시스템 프록시를 복원한다. |
| **대기** | 직접 연결 | 코어는 실행되며, 트래픽은 기본적으로 직접 연결된다. |
| **규칙 기반 인계** | 규칙 | 프로필의 규칙에 따라 트래픽을 분류한다. |
| **전체 인계** | 전역 | 모든 트래픽이 선택한 프록시를 거친다. |

투명 프록시(TUN)는 **설정 › 프록시**의 ‘투명 프록시’로 제어하며, 기본값은 켜짐이다. 켜져 있고 Mihomo 서비스를 사용할 수 있으면 인계는 TUN을 통해 이루어지며, 시스템 프록시를 읽지 않는 프로그램도 그 대상이 된다. Mihomo 서비스가 배포되지 않은 경우 인계는 Windows 시스템 프록시를 통해 이루어진다.

> [!CAUTION]
> 투명 프록시는 컴퓨터 전체의 라우팅과 DNS를 인계한다. Clash#는 ‘컴퓨터 한 대, 대화형 사용자 한 명’을 전제로 설계되었으며, 사용자 세션 간 트래픽 격리를 제공하지 않는다.

**설정 › 작업 표시줄 트레이**의 ‘트레이 색상 상태 표시 사용’을 켜면 트레이 아이콘이 색상으로 현재 상태를 나타낸다.

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="회색 트레이 아이콘" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="초록색 트레이 아이콘" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="보라색 트레이 아이콘" /> |
| :---: | :---: | :---: |
| 사용 안 함 또는 대기 | 시스템 프록시 | 투명 프록시 |

## 설치

**시스템 요구 사항**: Windows 11 x64, 또는 데스크톱 환경을 포함한 Windows Server 2025.

정식 버전이 릴리스되면 설치 패키지는 [Releases](https://github.com/Water-Run/ClashSharp/releases) 페이지에 게시된다.

1. 릴리스 패키지를 내려받아 압축을 해제한다. `ClashSharp-Installer.exe`는 같은 위치의 `payload` 폴더와 함께 두어야 한다.
2. 현재 사용자 세션에서 `ClashSharp-Installer.exe`를 실행한다. 앱은 설치 프로그램을 실행한 사용자에게 설치되며, 관리자 권한으로 실행하거나 .NET을 미리 설치할 필요는 없다.
3. Mihomo 서비스와 필요한 인증서 신뢰를 배포할 때 설치 프로그램이 관리자 승인을 요청한다. 승인 전에 표시된 게시자를 확인해야 한다.

복구, 업그레이드, 제거는 모두 같은 설치 프로그램을 다시 실행하여 수행한다.

> [!WARNING]
> 제거는 `ClashSharp-Installer.exe`를 통해 수행해야 한다. Windows의 ‘설정 › 앱’에서만 제거하면 Mihomo 서비스 등 컴퓨터 수준의 리소스가 함께 정리되지 않는다.

## 시작하기

1. **프록시 › 링크**에서 구독 링크를 추가한다. Clash#가 해당 프로필을 내려받아 검증한다.
2. **프록시 › 프로필**에서 해당 프로필이 현재 프로필인지 확인한다.
3. **메인 컨트롤**에서 **규칙 기반 인계**를 선택한다.
4. **프록시 › 노드**에서 노드를 선택한다. 선택 전에 지연 시간 측정을 실행할 수 있다.

Clash#는 시작할 때마다 시작 안내를 표시하여 구독, 투명 프록시 서비스, 시작 시 복원, 남은 프록시 설정의 점검 결과를 요약한다. 이 안내는 **설정 › 시작**에서 끌 수 있다.

로컬 혼합 포트의 기본값은 `10000`이며, HTTP와 SOCKS가 함께 사용한다. 시스템 프록시를 읽지 않는 프로그램은 `127.0.0.1:10000`을 직접 사용할 수 있다. 포트는 **설정 › 프록시**에서 변경한다.

기본 창을 닫으면 Clash#는 기본적으로 트레이로 최소화되어 계속 실행된다. 종료에는 트레이 메뉴의 ‘안전하게 종료’를 사용한다. 종료 시에는 기본적으로 코어가 중지되고 시스템 프록시가 복원된다.

### 페이지

| 페이지 | 용도 |
| :--- | :--- |
| **메인 컨트롤** | 모드 전환. 타일로 코어 상태, 속도, 트래픽, 지연 시간, 구독 사용량, 공인 IP 등을 표시한다. |
| **프록시** | 노드, 프로필(버전 기록 포함), 구독 링크, 규칙. |
| **트리거** | 자동화 작업. 위에서 아래로 순서대로 판단한다. |
| **연결** | 활성 연결과 각 연결의 프로세스, 일치한 규칙, 프록시 경로. |
| **통계** | 프로필, 노드, 시간별로 집계한 장기 통계. 시스템 로그도 여기에서 연다. |
| **설정** | 언어와 외관, 시작, 프록시, Windows 네이티브 보정, 알림, 트레이, 데이터 백업. |

## 자주 묻는 질문

<details>
<summary><b>WSL, 터미널, Microsoft Store 앱이 프록시를 통해 네트워크에 접근하지 못한다</b></summary>
<br />

해당 프로그램은 Windows 시스템 프록시를 읽지 않는다. **설정 › Windows 네이티브**에서 각 항목을 진단하고 결과에 따라 보정을 적용할 수 있으며, 보정은 언제든 되돌릴 수 있다. 투명 프록시를 켜면 이러한 프로그램의 트래픽도 인계된다.
</details>

<details>
<summary><b>시작 시 충돌이 보고된다</b></summary>
<br />

충돌 확인은 네 가지 경우를 다룬다. 외부 mihomo 프로세스, 사용 중인 프록시 포트, 켜져 있는 Windows 수동 프록시, 다른 TUN 또는 VPN 어댑터이다. 대화 상자는 항목별로 설명하고, 안전하게 처리할 수 있는 항목에는 수정 동작을 제공한다. TUN과 VPN 어댑터는 알림만 표시하며, Clash#가 자동으로 끄지 않는다. 확인은 **설정 › 시작**에서 언제든 다시 실행할 수 있다.
</details>

<details>
<summary><b>비정상 종료 후 시스템 프록시가 켜진 상태로 남아 있다</b></summary>
<br />

Clash#가 비정상 종료되면 복구 프로세스가 Clash#에 속한 시스템 프록시를 즉시 복원한다. 그 전에 컴퓨터가 종료된 경우에는 다음 실행 시 Clash#가 남은 설정을 점검하고 정리한다. 또한 **설정 › 시작**에서 ‘시작 시 복원 도우미’를 등록하면, Clash# 실행을 기다리지 않고 Windows 로그인 시점에 같은 정리가 실행된다.
</details>

<details>
<summary><b>지역 이름이나 국기가 프로필과 다르게 표시된다</b></summary>
<br />

**설정 › 중국 본토 특화 기능**은 기본적으로 국기 대체와 텍스트 보완을 사용하며, 중국 본토의 규범에 따라 인터페이스에 표시되는 일부 지역의 이름과 국기를 조정한다. 이 기능은 화면 표시에만 적용되며, 프로필, 로그, 검색, 복사한 내용, 내보낸 데이터는 변경하지 않는다. 같은 위치에서 끌 수 있다.
</details>

<details>
<summary><b>백업과 업데이트</b></summary>
<br />

**설정 › 데이터**에서 Clash# 설정을 하나의 파일로 내보낼 수 있으며, 프록시 구성과 구독을 함께 포함할 수 있다. 내보낸 파일은 이후 가져와 복원한다. 로그는 SQLite 데이터베이스로 따로 내보낼 수 있으며, 가져오기는 지원하지 않는다.

**정보** 페이지는 GitHub Releases에서 새 버전을 확인한다. 업데이트는 설치 프로그램이 수행한다.
</details>

## 소스에서 빌드

Windows x64, PowerShell 7, 그리고 [`global.json`](./global.json)에 지정된 버전의 .NET SDK가 필요하다.

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

설치 패키지 빌드 절차는 [Installer/README.md](./ClashSharp/Installer/README.md)(중국어)에 있다.

<details>
<summary><b>저장소 구성</b></summary>
<br />

| 경로 | 내용 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 본 프로그램 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 도메인 모델, 애플리케이션 로직, Windows 및 저장소 어댑터 |
| `ClashSharp/ClashSharp.MihomoService` | 투명 프록시용 코어를 실행하는 Windows 서비스 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 비정상 종료 후 시스템 프록시를 복원하는 복구 프로세스 |
| `ClashSharp/ClashSharp.Installer*` | WPF 설치 프로그램과 트랜잭션 로직 |
| `ClashSharp/SandboxTest` | Windows 샌드박스 기반 설치 패키지 스모크 테스트 |
| `docs/` | 설계 기록과 개발 기록(중국어) |

</details>

코딩 규칙은 [CodingStyle.md](./CodingStyle.md)에 있다.

## 라이선스

프록시 코어는 MetaCubeX의 [mihomo](https://github.com/MetaCubeX/mihomo)이며 GPL-3.0으로 배포된다. GeoIP와 GeoSite 데이터는 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)에서 제공된다.

Clash#는 [AGPL-3.0](./LICENSE) 라이선스로 공개된다.
