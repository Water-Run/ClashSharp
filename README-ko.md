<div align="center">

<img src="./ClashSharp/ClashSharp/Assets/Logo.svg" alt="Clash# logo" width="112" />

# Clash#

**Windows를 위해 만든 프록시 클라이언트. [mihomo](https://github.com/MetaCubeX/mihomo) 코어 기반.**

[![CI](https://img.shields.io/github/actions/workflow/status/Water-Run/ClashSharp/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square)](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-AGPL--3.0-2ea043?style=flat-square)](./LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows%2011-x64-0078D4?style=flat-square&logo=windows11&logoColor=white)](#설치)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](./global.json)

[English](./README.md) · [简体中文](./README-zh.md) · [繁體中文](./README-zh-Hant.md) · **한국어** · [Русский](./README-ru.md) · [فارسی](./README-fa.md)

</div>

> [!IMPORTANT]
> Clash# 1.0.0은 아직 개발 중이며, 정식 버전은 나오지 않았습니다.
> CI가 만드는 패키지는 서명되지 않은 테스트 빌드이므로 일상적으로 쓰기에는 적합하지 않습니다.

대부분의 프록시 클라이언트는 어디서나 돌아가도록 만들어집니다. Clash#는 단 하나의 운영체제에서, 그곳에서만큼은 제대로 돌아가도록 만들어졌습니다.

C#과 WinUI 3로 작성되어, 잠시 머무는 손님이 아니라 처음부터 Windows 11의 일부였던 것처럼 보입니다. Windows 사용자라면 언젠가 한 번쯤 걸려 넘어지는 문제들도 잘 알고 있습니다. 시스템 프록시를 무시하는 프로그램, 자기만의 네트워크에 사는 WSL, `localhost`에 닿지 못하는 Store 앱, 그리고 클라이언트가 멈춘 뒤에도 여전히 켜져 있는 시스템 프록시 같은 것들 말이지요.

Clash 구독 하나만 준비하세요. 나머지는 Clash#가 맡습니다.

## 특징

- **Windows 11에 자연스럽게 녹아듭니다.** 네이티브 컨트롤, Fluent 아이콘, 반투명 소재, 밝은 테마와 어두운 테마, 그리고 내가 고른 강조 색. 첫 화면은 ‘빠른 설정’을 닮은 타일 모음입니다. 필요한 타일만 남기고 원하는 자리로 끌어다 놓으세요.
- **들어올 때 모습 그대로 두고 나갑니다.** 시작하기 전에 방해되는 것이 없는지 먼저 살핍니다. 다른 mihomo, 이미 사용 중인 포트, 남아 있는 수동 프록시, 다른 VPN 어댑터 같은 것들이죠. 혹시 예기치 않게 종료되더라도 작은 감시 프로그램이 남겨진 시스템 프록시를 곧바로 꺼 줍니다.
- **Windows의 오래된 빈틈을 메웁니다.** 클릭 한 번이면 WSL, 터미널, Microsoft Store 앱도 프록시를 사용합니다.
- **알아서 움직이게 할 수도 있습니다.** 트리거는 ‘이럴 때 이렇게 하라’는 작은 규칙입니다. 예를 들어, *이번 실행의 트래픽이 5 GB를 넘으면 대기로 바꾸고 알려 줘.*
- **기억합니다.** 트래픽, 연결, 노드 상태가 로컬 SQLite에 저장되므로, 통계 화면은 지금 이 순간 너머까지 보여 줍니다.
- **당신의 언어로 말합니다.** 한국어를 비롯해 중국어 간체와 번체, 영어, 러시아어, 프랑스어, 독일어, 그리고 오른쪽에서 왼쪽으로 쓰는 페르시아어까지.

## 모드

Clash#는 모드마다 ‘네트워크에 무엇을 하는지’를 이름으로 붙였습니다. 다른 Clash 클라이언트를 써 보셨다면 이렇게 대응됩니다.

| Clash#에서 | 흔히 부르는 이름 | 하는 일 |
| :--- | :--- | :--- |
| **사용 안 함** | 끄기 | 코어를 멈추고 시스템 프록시를 원래대로 돌려놓습니다. |
| **대기** | 직접 연결 | 코어는 돌지만 트래픽은 그대로 직접 나갑니다. |
| **규칙 기반 인계** | 규칙 | 프로필의 규칙에 따라 트래픽을 나눕니다. |
| **전체 인계** | 전역 | 모든 트래픽이 선택한 프록시를 거칩니다. |

**투명 프록시**(TUN 모드)는 *설정 › 프록시*에 있는 별도의 스위치입니다. 시스템 프록시를 거들떠보지 않는 프로그램까지 포함해 모든 앱의 트래픽을 받아 냅니다. 만약 시작에 실패하면 Clash#가 알아서 시스템 프록시로 물러설 수 있습니다.

> [!CAUTION]
> 투명 프록시는 컴퓨터 전체의 라우팅과 DNS를 넘겨받습니다. Clash#는 ‘컴퓨터 한 대에 로그인한 사용자 한 명’을 전제로 설계되었으며, 사용자별로 트래픽을 분리하지 않습니다.

설정에서 ‘트레이 색상 상태 표시’를 켜 두면, 아이콘만 봐도 지금 상태를 알 수 있습니다.

| <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Inactive.svg" width="24" alt="회색 트레이 아이콘" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.SystemProxy.svg" width="24" alt="초록색 트레이 아이콘" /> | <img src="./ClashSharp/ClashSharp/Assets/Tray/Logo.Tun.svg" width="24" alt="보라색 트레이 아이콘" /> |
| :---: | :---: | :---: |
| 사용 안 함 또는 대기 | 시스템 프록시 | 투명 프록시 |

## 설치

**필요한 환경:** Windows 11 x64, 또는 데스크톱 환경이 포함된 Windows Server 2025.

1.0.0이 나오면 [Releases](https://github.com/Water-Run/ClashSharp/releases) 페이지에서 패키지를 받을 수 있습니다.

1. 패키지를 내려받아 압축을 풉니다. `ClashSharp-Installer.exe`와 `payload` 폴더는 반드시 나란히 두세요.
2. 본인 계정에서 `ClashSharp-Installer.exe`를 더블클릭합니다. Clash#는 설치 프로그램을 실행한 사람에게 설치되므로 ‘관리자 권한으로 실행’할 필요도, .NET을 미리 설치할 필요도 없습니다.
3. 투명 프록시에 쓰이는 백그라운드 서비스를 설치하기 위해 Windows가 한 번 권한을 요청합니다. 게시자가 예상한 곳이 맞는지 확인한 뒤 허용하세요.

나중에 복구하거나 업데이트하거나 제거하고 싶다면, 같은 설치 프로그램을 다시 실행하면 됩니다.

> [!WARNING]
> 제거는 *설정 › 앱*이 아니라 `ClashSharp-Installer.exe`로 해 주세요. Windows에서 앱만 지우면 백그라운드 서비스가 남습니다.

## 시작하기

1. **프록시 › 링크**에서 구독 링크를 추가합니다. Clash#가 그 뒤에 있는 프로필을 받아 옵니다.
2. **프록시 › 프로필**에서 그 프로필이 선택되어 있는지 확인합니다.
3. **메인 컨트롤**로 돌아가 **규칙 기반 인계**를 고릅니다. 이제 프록시를 통해 인터넷을 쓰고 있습니다.
4. **프록시 › 노드**에서 노드를 고르거나, 지연 시간을 측정해 빠른 노드를 찾아보세요.

Clash#는 시작할 때마다 모든 준비가 되었는지 짧은 점검 목록으로 확인해 줍니다. 더 필요 없어지면 *설정 › 시작*에서 끌 수 있습니다.

시스템 프록시를 따르지 않는 프로그램은 `127.0.0.1:10000`을 직접 가리키면 됩니다. HTTP와 SOCKS가 이 포트 하나를 함께 씁니다. 포트 번호는 *설정 › 프록시*에서 바꿀 수 있습니다.

### 화면 둘러보기

| 화면 | 하는 일 |
| :--- | :--- |
| **메인 컨트롤** | 모드 전환. 속도, 트래픽, 지연 시간, 구독 사용량, 공인 IP 등을 타일로 보여 줍니다. |
| **프록시** | 노드, 프로필(각각 버전 기록 포함), 구독 링크, 규칙. |
| **트리거** | 자동화 규칙. 위에서 아래로 차례대로 판단합니다. |
| **연결** | 지금 열려 있는 모든 연결과 그 프로세스, 일치한 규칙, 거쳐 간 경로. |
| **통계** | 프로필별, 노드별, 시간대별로 쌓인 장기 통계. |
| **설정** | 언어와 테마, 시작 동작, 투명 프록시, Windows 보정, 알림, 트레이, 백업. |

## 알아 두면 좋은 것들

<details>
<summary><b>브라우저는 되는데 WSL, 터미널, Store 앱은 안 돼요</b></summary>
<br />

이 프로그램들은 Windows 시스템 프록시를 읽지 않습니다. *설정 › Windows 네이티브*를 열면 Clash#가 WSL, 터미널, Microsoft Store를 하나씩 점검하고, 항목마다 따로 보정을 적용하거나 되돌릴 수 있습니다.
</details>

<details>
<summary><b>시작할 때 충돌이 있다고 나와요</b></summary>
<br />

Clash#가 하려는 일을 다른 무언가가 이미 하고 있다는 뜻입니다. 다른 mihomo 코어, 10000번 포트를 쥐고 있는 프로그램, 켜져 있는 수동 프록시, 또는 VPN 어댑터일 수 있습니다. 대화 상자가 항목마다 설명해 주고, 안전하게 고칠 수 있는 것에는 수정 버튼을 붙여 줍니다. VPN 어댑터는 알려 주기만 할 뿐, Clash#가 대신 끄는 일은 없습니다. 점검은 *설정 › 시작*에서 언제든 다시 할 수 있습니다.
</details>

<details>
<summary><b>Clash#가 멈춘 뒤에도 프록시가 켜져 있어요</b></summary>
<br />

원래는 그럴 일이 없습니다. Clash#가 비정상적으로 종료되는 순간 감시 프로그램이 시스템 프록시를 되돌려 놓기 때문입니다. 그보다 먼저 컴퓨터가 꺼졌다면, 다음에 Clash#를 실행할 때 남은 흔적을 정리합니다. *설정 › 시작*에서 작은 도우미를 등록해 두면 Clash#를 열기 전, Windows에 로그인하는 시점에 같은 정리를 해 줍니다.
</details>

<details>
<summary><b>일부 지역 이름과 국기가 프로필과 다르게 보여요</b></summary>
<br />

기본으로 켜져 있는 ‘중국 본토 특화 기능’ 설정 때문입니다. 이 설정은 중국 본토의 관례에 따라 인터페이스에 표시되는 일부 지역의 이름과 국기를 바꿉니다. 바뀌는 것은 화면 표시뿐이며, 프로필, 로그, 검색, 복사한 텍스트, 내보낸 데이터는 그대로입니다. 원하지 않으면 설정에서 끌 수 있습니다.
</details>

<details>
<summary><b>백업과 업데이트</b></summary>
<br />

*설정 › 데이터*에서 설정을 파일 하나로 내보낼 수 있고, 원하면 프로필과 구독까지 함께 담아 나중에 다시 가져올 수 있습니다. 로그는 SQLite 데이터베이스로 따로 내보낼 수 있습니다.

*정보* 화면은 GitHub Releases에서 새 버전을 확인합니다. 업데이트 설치는 설치 프로그램이 맡습니다.
</details>

## 소스에서 빌드하기

Windows x64, PowerShell 7, 그리고 [`global.json`](./global.json)에 고정된 버전의 .NET SDK가 필요합니다.

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build   ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test    ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
```

설치 패키지를 만들려면 몇 단계가 더 필요합니다. [Installer/README.md](./ClashSharp/Installer/README.md)(중국어)를 참고하세요.

<details>
<summary><b>저장소 구성</b></summary>
<br />

| 경로 | 내용 |
| :--- | :--- |
| `ClashSharp/ClashSharp` | WinUI 3 데스크톱 앱 |
| `ClashSharp/ClashSharp.Core` · `.Application` · `.Infrastructure` | 도메인 모델, 애플리케이션 로직, Windows 및 저장소 어댑터 |
| `ClashSharp/ClashSharp.MihomoService` | 투명 프록시용 코어를 실행하는 Windows 서비스 |
| `ClashSharp/ClashSharp.RecoveryWatchdog` | 비정상 종료 후 시스템 프록시를 되돌리는 감시 프로그램 |
| `ClashSharp/ClashSharp.Installer*` | WPF 설치 프로그램과 트랜잭션 로직 |
| `ClashSharp/SandboxTest` | Windows 샌드박스에서 돌리는 패키지 스모크 테스트 |
| `docs/` | 설계 기록과 개발 기록(중국어) |

</details>

기여는 언제든 환영합니다. 먼저 [CodingStyle.md](./CodingStyle.md)를 읽어 주세요.

## 감사의 말과 라이선스

프록시 코어는 MetaCubeX의 [mihomo](https://github.com/MetaCubeX/mihomo)이며 GPL-3.0으로 배포됩니다. GeoIP와 GeoSite 데이터는 [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat)에서 가져옵니다.

Clash#는 [AGPL-3.0](./LICENSE) 라이선스로 공개됩니다.
