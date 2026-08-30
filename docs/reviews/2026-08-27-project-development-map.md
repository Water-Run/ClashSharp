# ClashSharp 项目开发地图

- 测绘日期：2026-08-27
- 审计起始基线：`main@fa72287`（仅标识开始测绘时已存在的提交，不是本文自身提交）
- 当前候选：本文所在候选批次，包含审计起始基线之上的托盘视觉、依赖修复、Installer runner、测试产物隔离和文档改动
- 发布目标：`Core Production Ready`，即核心代理、安装/修复/升级/卸载和发布产物具备可恢复、可复现、可验证的 Windows 生产闭环
- 执行队列：[`2026-08-27-production-readiness-execution-plan.md`](./2026-08-27-production-readiness-execution-plan.md)
- 历史审计：[`2026-08-10-development-audit-and-optimization-roadmap.md`](./2026-08-10-development-audit-and-optimization-roadmap.md)
- Installer 重写审查：[`2026-08-30-installer-wpf-rewrite-audit.md`](./2026-08-30-installer-wpf-rewrite-audit.md)
- 架构关闭证据：[`stabilization-ledger.md`](../architecture/stabilization-ledger.md)

## 1. 判定口径

本地图把实现状态和生产证据分开，避免“代码存在”被误判成“生产可用”。

| 级别 | 含义 | 可以证明什么 |
|---|---|---|
| E0 | 尚无实现或证据 | 只能作为计划项 |
| E1 | 代码/资产已实现并经过静态审查 | 设计已落地，不能证明可运行 |
| E2 | 本地自动化或确定性契约通过 | 核心逻辑成立，不能替代 Windows 集成 |
| E3 | 被明确引用的目标提交通过 Windows CI | 该目标可在固定工具链构建和测试，不能把证据自动外推到其他提交，也不能替代真实签名安装 |
| E4 | 最终签名产物在隔离 Windows VM/真机通过 | 可以作为 RC/发布门禁证据 |

状态只使用以下含义：

- `已完成`：对应退出条件已有可追溯证据。
- `候选完成`：实现已完成，但仍缺 E3 或 E4。
- `进行中`：已有部分实现或证据，退出条件未闭合。
- `未开始`：尚无满足目标的实现。

事实优先级为：同一候选提交的 CI/发布证据 > 本文所在候选批次的可复现验证 > 源码静态证据 > 历史审计文本。若数字不同，必须同时标明基线，不跨基线合并。

## 2. 一页结论

| 维度 | 当前判断 | 证据级别 | 到生产就绪的主要缺口 |
|---|---|---:|---|
| 产品功能面 | 主控、节点、配置、订阅、规则、触发器、连接、统计、日志、设置等主体功能已形成 | 候选 E2；审计起始基线 E3 | 真实代理/TUN、恢复、升级与卸载的签名 VM 验收 |
| 架构与运行时 | 分层、组合根、类型安全导航、Core owner、mutation/journal 基础较成熟 | 候选 E2；审计起始基线 E3 | 生命周期长尾、设置 generation、页面取消/释放和安全边界加固 |
| Installer 安全基础 | Rust 发布 authority 已有防降级、identity、payload allowlist；C# 候选已落地 strict manifest/lease、Windows 11 x64 policy、SafeFileHandle 与证书 adapter | 候选 E1/E2；审计起始基线 E3 | C# package/machine/elevation composition、对称 durable uninstall、Windows/VM 故障注入 |
| 托盘与品牌资产 | 三态色彩、有效运行态判定、设置名称、SVG Logo、多尺寸 ICO 已在本文所在候选批次实现 | E2 | 候选提交 Windows CI、Explorer/高 DPI 人工 smoke |
| CI | 单一 Windows CI 覆盖 .NET 与两个 Rust crate 的格式、构建、测试和审计 | E3（审计起始基线） | 审计起始基线总体失败；本文所在候选批次的无-ignore audit 尚未由 CI 验证 |
| Release | 无 tag、无 GitHub Release、无 release workflow、无受保护发布环境 | E0 | 签名、SBOM、provenance、VM promotion、发布与回滚手册整套闭环 |

综合判定：**功能候选基本成形，发布候选尚未成立；当前不能标记生产就绪。** 最短关键路径不是继续扩展页面，而是先恢复绿色基线，再依次关闭 Installer durable lifecycle、真实 Windows E2E 和签名发布流水线。

## 3. 产品与运行时拓扑

```text
ClashSharp-Installer.exe                 唯一用户可见部署入口
  └─ 签名 payload
      └─ ClashSharp MSIX
          └─ ClashSharp.exe             唯一用户可见主体应用
              ├─ App-owned mihomo       非 TUN 模式
              ├─ ClashSharp.MihomoService.exe
              │   └─ Service-owned mihomo  TUN 模式
              ├─ ClashSharp.RecoveryWatchdog.exe
              └─ mihomo.exe + GeoData

测试边界（不得进入发布 payload）
  ├─ Process/Startup/Trigger/Settings Probe
  └─ SandboxTest
```

运行时所有权规则：

- Disabled/Standby 不表达系统接管；Rule/Full 在 TUN 未生效而 App-owned Core 与 WinINet proxy 已验证时表达系统代理。
- TUN 开启时 Core owner 切换为 Windows Service；主体通过认证 IPC/控制器协同，不取得 Service 部署权限。
- Installer 是 service、machine payload、association、package 和证书生命周期的唯一部署 authority。
- Recovery Watchdog 只负责同用户异常退出后的有限恢复，不是第二 Installer 或常驻产品入口。

## 4. 仓库与依赖地图

审计起始基线的解决方案包含 11 个 .NET 项目，另有 2 个 Rust crate。2026-08-30 的迁移候选新增 Core、纯 Presentation、WPF、Windows adapter 及其三组测试工程，因此当前 solution 候选为 18 个 .NET 项目；新 WPF runtime 在 Windows package/machine authority 与 E3/E4 接通前保持 fail-closed。

| 组件 | 责任 | 直接依赖/产物关系 | 发布身份 |
|---|---|---|---|
| `ClashSharp.Core` | 领域模型、稳定基础契约 | 无项目依赖 | 库 |
| `ClashSharp.Application` | 用例、协调、端口接口 | `Core` | 库 |
| `ClashSharp.Infrastructure` | SQLite、文件、进程、网络适配器 | `Application`、`Core` | 库 |
| `ClashSharp` | WinUI 3、ViewModel、组合根和应用服务 | 三层库；构建 Service/Watchdog 资产 | 主体 MSIX |
| `ClashSharp.MihomoService` | LocalSystem 内部服务宿主 | `Core`、`Infrastructure` | MSIX 内部资产，由 Installer 管理 |
| `ClashSharp.RecoveryWatchdog` | 同用户一次性恢复助手 | 独立小型宿主 | MSIX 内部资产 |
| 四个 `*Probe` | 跨进程集成测试探针 | 测试场景依赖 | test-only，不可 publish/pack |
| `ClashSharp.Tests` | unit/integration/architecture 测试 | 主体、三层库、Service、Watchdog、Probe | 测试项目 |
| `ClashSharp.Installer.Core` | C# Installer strict protocol、durable coordinator、Windows 11 x64 policy、embedded manifest/locked lease 与平台端口 | 无 Windows UI 依赖 | 迁移候选库 |
| `ClashSharp.Installer.Presentation` | 单产品卡片的纯状态机、命令和 readiness/result 边界 | `Installer.Core` | 迁移候选库，可在 Linux 直接测试 |
| `ClashSharp.Installer.Presentation.Tests` | 单卡片状态、single-flight/cancel/progress race 与 executable project contract | Core、Presentation | test-only |
| `ClashSharp.Installer` | self-contained WPF 单卡片 shell 与 native platform probe | Core、Presentation | 迁移预览；当前不可执行系统 mutation |
| `ClashSharp.Installer.Tests` | 直接引用 Core 产物的 protocol/coordinator/file-store 测试 | `Installer.Core` | test-only |
| `ClashSharp.Installer.Windows` | SafeFileHandle payload lease、CurrentUser certificate adapter；后续 Package/SCM/elevation | `Installer.Core` | 迁移候选 Windows adapter |
| `ClashSharp.Installer.Windows.Tests` | 真实 Win32 sharing/rename/payload/certificate/SID 边界 | Core、Windows adapter | Windows test-only |
| `Installer` | Rust + Slint 安装/维护入口 | 消费精确签名 payload | 唯一发布 Installer |
| `SandboxTest` | Windows 安装场景 host | 调用测试脚本/产物 | test-only，`publish=false` |

代码规模用于识别审查热点，不作为质量 KPI。2026-08-27 开始测绘时的一次性快照为：732 个 C# 源文件、20 个 XAML 文件、18 个 Rust 源文件；`ClashSharp.Tests` 有 1,506 个 `[Fact]`/`[Theory]` 声明；主体和测试分别约 56.9k 与 55.4k 行 C#。这些数字不冒充本文所在提交的实时计数；后续分析应从目标提交重新扫描，而不是手工沿用快照。快照说明已有较强测试投入，也说明发布结论应以风险证据而非测试数量作判断。

主要复杂度热点：`SettingsViewModel`、`ProfileCatalogService`、Installer `main.rs`/`service_plan.rs`、`MihomoChildSupervisor`、配置和数据包事务。首个 RC 只拆分影响资源所有权、安全边界或可测性的部分，不以一次性“大文件清零”阻断生产闭环。

## 5. 功能—代码—证据—缺口矩阵

| 功能域 | 当前实现 | 已有证据 | 状态 | 下一道生产门禁 | 责任域 |
|---|---|---|---|---|---|
| 启动、单实例、退出恢复 | AppHost、secondary-instance 隔离、mutation/lifecycle 协调、Watchdog | architecture/unit/integration 测试；重复双进程证据 | 进行中；候选 E2，审计起始基线 E3 | 打包真实应用启动、崩溃、Explorer/登录恢复 smoke | Application/Runtime |
| 主控与接管模式 | Disabled、Standby、Rule、Full、TUN；显式 Core owner | takeover、rollback、ownership、service IPC 测试 | 候选完成；候选 E2，审计起始基线 E3 | 签名 VM 验证 system proxy、TUN、重启和退出恢复 | Runtime/QA |
| 节点、配置、订阅、规则 | 页面、CRUD、导入/校验、调度、历史/回滚已存在 | ViewModel、catalog、配置事务测试 | 进行中；候选 E2，审计起始基线 E3 | 修复 Profile gate 与页面 CTS 生命周期；升级/回滚 soak | Application/Presentation |
| 触发器 | SQLite 持久化、matcher、scheduler、outbox 和 crash recovery | stabilization ledger P1-02～P1-05 已关闭 | 已完成；审计起始基线 E3 | 纳入签名包真实运行 smoke，不重新打开已关闭架构项 | Runtime |
| Connections | `/connections` WebSocket、采样与页面刷新已实现 | client、sampling、ViewModel 测试 | 进行中；候选 E2，审计起始基线 E3 | latest-wins refresh 与 serialized mutation 分离；反复导航/取消测试 | Runtime/Presentation |
| Statistics 与 Logs | SQLite 持久化；`/logs` WebSocket；清理与展示链路 | repository/storage/client/ViewModel 测试 | 进行中；候选 E2，审计起始基线 E3 | 存储入口统一限长、控制字符清理和 token/URL/path 脱敏 | Runtime/Security |
| Settings 与维护动作 | 注册表式 key 契约、LocalSettings 偏好、JSON generation/desired-applied 基础 | registry、codec、repository、rollback 测试 | 进行中；候选 E2，审计起始基线 E3 | Phase 05 generation/并发矩阵；安全边界和取消所有权 | Application |
| 托盘状态与品牌 | SVG Logo；灰/绿/C# 紫三态；只读取 mutation owner 的 verified effective state；8 帧 ICO；六语言设置文本 | effective/unknown/fallback 状态测试、资源/ICO 契约、SVG XML 校验 | 候选完成，E2 | 本文所在候选批次的 Windows CI；100%/200% DPI、Explorer 重启、设置迁移 smoke | Presentation/QA |
| Installer 基础安全 | 唯一 authority、防降级、identity 单源、精确 payload、immutable handle | Rust 契约/攻击性测试与此前 Windows CI | 已完成；候选 E2，审计起始基线 E3 | 保持回归门禁 | Installer/Security |
| Installer 进程执行 | 统一 deadline、有界双流 capture、非提权进程树 Job 终止、稳定诊断码 | 候选批次独立 runner 测试和 Windows target clippy | 候选完成，E2 | Windows 非提权孙进程测试；跨 `RunAs` 提权边界的 durable cut-point/VM 证据；timeout 后 journal Repair 收敛 | Installer |
| Installer durable lifecycle | install/uninstall v2 journal、受保护 ProgramData 写入；C# MachineReserved 前置、卸载 MachineRemovalAuthorized、canonical journal digest、helper-authoritative result journal、result + protected-store reload 后才推进的 two-phase session、helper-only package commit、certificate ownership、locked release、package identity 与 Windows certificate/MSIX adapter | 最近 Core 499/499、Presentation 39/39 实际全绿；当前 static 512/70/103 已加入 strict parent-PID bootstrap、protected pipe DACL/first-instance、双向 PID primitive、跨 Rust/App/Service 的 pipe-name 固定向量、exact-target-SID package commit inspector 与覆盖全部合法 phase 的单卡片状态决策，前 76 项 Windows checkpoint Release 交叉编译 0 warning/error；Core checkpoint line 94.90% / branch 85.67% | 进行中；候选 E1/E2，审计起始基线 E3 | 先关闭 asInvoker parent 与 protected writer 的 authority mismatch，再接 signed NativeAOT helper、persistent authenticated broker、SCM/machine mutation/final composition，并重验最新 512/70/103 与真实 cut-point/VM | Installer |
| 本地化与可访问性 | 六语言 catalog 和基础 WinUI 语义 | 资源完整性与部分 UI 测试 | 进行中，E2 | 键盘、Narrator、高对比度、200% 文本与 Installer 控件验收 | Presentation/QA |
| 发布与运维 | 开发打包脚本具备精确 staging/signing 输入契约 | build/Rust 契约测试 | 未开始，E0/E2 | .NET runtime、可信 GeoData、release workflow、受保护环境、签名 VM、SBOM/provenance/runbook | Release |

## 6. 状态与副作用所有权

| 状态/副作用 | 权威 owner | 持久化/边界 | 恢复契约 | 当前风险 |
|---|---|---|---|---|
| UI 偏好 | App | WinUI `ApplicationData.LocalSettings`，键由 `SettingsRegistry` 管理 | 兼容旧键和默认值 | 迁移需保留向后兼容 |
| 核心设置 desired/applied | Application + Infrastructure | generation 根中的验证 JSON envelope | 原子写、identity/path policy、rollback | 完整 generation 并发矩阵待关闭 |
| Profiles/Links/Rules | App catalog service | 应用数据文件、历史和调度元数据 | 导入校验、历史、回滚 | gate 生命周期和大类复杂度 |
| Triggers | Runtime | SQLite + durable outbox/backup | crash recovery 和 replay | 已有关闭证据，关注发布回归 |
| Logs/Statistics | Runtime | SQLite | maintenance/cleanup | 脱敏与容量门禁不足 |
| system proxy/TUN | 当前 Core owner + mutation coordinator | Windows 状态和 ownership journal | 正常退出、Watchdog、登录兜底 | 多用户不支持；真实重启证据不足 |
| Service/machine payload | Installer | 固定 Program Files/ProgramData 根和 association | roll-forward Repair | durable uninstall 尚不对称 |
| install transaction | Installer | 受保护的 machine journal；C# 独立 certificate ownership ledger | 同版本/显式 Repair；证书 import/delete write-ahead replay | Windows/Rust authority 接线和真实 cut point 未覆盖 |
| release artifact | Release pipeline | 随机 staging → hash-identical promotion | 校验和、provenance、重建 | 当前没有正式 pipeline 或发布环境 |

## 7. 质量与发布证据快照

### 7.1 已提交基线

审计起始基线 `main@fa72287` 的 Windows CI 运行于 2026-08-24：

- .NET format/build/test 通过，TRX 为 2,212/2,212 通过。
- Installer fmt/clippy/test 通过，日志合计 80 个 Rust 测试通过。
- SandboxTest fmt/clippy/test 通过，11/11 通过。
- 总体 workflow 失败，唯一失败 job 是 Installer dependency audit：`webbrowser 1.2.1` 命中 `RUSTSEC-2026-0257`。

因此审计起始基线不能标记绿色。本文所在候选批次已更新到 `webbrowser 1.2.2`、清除已经失效的 `quick-xml` RustSec exceptions，并以无 vulnerability ignore 的 audit 通过本地扫描；只有候选提交的同一 SHA Windows CI 全绿才可关闭 P0-A。

### 7.2 本文所在候选批次的证据

| 范围 | 当前证据 | 尚不能证明 |
|---|---|---|
| 托盘状态 | 覆盖 unknown、Disabled/Standby、已验证 system proxy、TUN、TUN fallback 和关闭色彩开关的解析矩阵；SVG XML 和三套 ICO 的 8 档 frame 结构通过 | Windows 通知区实际渲染、Explorer 重启、高 DPI |
| Installer runner | 8 个独立测试通过（含 teardown grace/worker detach 上界和无无界 wait/join 静态契约）；Windows target all-target clippy `-D warnings` 通过 | Windows Job 非提权孙进程终止；跨 `RunAs` 提权边界的 durable cut-point/VM 证据；journal cut-point 收敛 |
| 依赖 | locked metadata 和固定版 `cargo-audit 0.22.2` 无 vulnerability ignore 通过 | GitHub CI 候选提交绿色 |
| Probe/Sandbox 隔离 | 项目属性和架构/manifest 静态门禁 | 最终 release artifact 的 promotion 证明 |
| .NET 与源码卫生 | 官方 .NET SDK `10.0.201`；最近 Core 499/499、Presentation 39/39，均 0 skipped；Core checkpoint line 94.90%（3108/3275）/ branch 85.67%（1495/1745）；Windows 前 76 项测试 checkpoint 与 WPF 项目 Release 交叉编译 0 warning/error；当前静态 512/70/103，`git diff --check` 通过 | 交接前最新源码 checkpoint 因 6476 MiB < 12288 MiB 被资源门阻止；Windows XAML runtime/UIA、证书/AppXSVC/UAC/SCM、Windows test 与签名 VM；Linux 交叉编译不替代 E3/E4 |

本文所在候选批次已用官方 .NET SDK `10.0.201` 完成上述 Linux 可行验证；Linux 仍不能替代 WinUI XAML compiler、Windows Job、AppXSVC、UAC 和 SCM。因此这里明确保留 E3/E4 缺口，不以局部验证代替 CI/VM。

### 7.3 发布基础设施现状

- GitHub 仅有一个 active CI workflow。
- `main` 未启用 branch protection。
- 没有 GitHub Environment、release workflow、tag 或正式 GitHub Release。
- Installer Core 已有 line 90% / branch 80% 阈值；仓库其余关键域仍缺统一阈值，且没有签名 promotion 门禁、SBOM/provenance 产出和真实 Installer VM 必需检查。
- Service/Watchdog 的正式形态已改为 self-contained single-file；App 与干净 Windows VM 的整体 runtime 证据仍未闭环。
- 正式构建强制要求四项 GeoData；干净 checkout 不含它们，准备脚本也没有固定可信上游与预期 digest。

这些不是普通文档债务：runtime/GeoData 归入 P0-D，不可变候选与签名供应归入 P0-F，真实 VM 门禁与受保护发布分别归入 P0-G/P0-H；覆盖率等质量门禁归入 RC 加固。

## 8. 本文所在候选批次的可审查切片

本文所在候选批次包含多个关注点，不把它们伪装成已经存在的 GitHub PR。建议按以下 work batch 独立审查和取证：

| Batch | 内容 | 关键文件/边界 | 合并门禁 |
|---|---|---|---|
| WB-000 | Installer 依赖公告修复和失效 exception 清理 | `Cargo.lock`、CI、audit policy | no-ignore locked audit + Windows CI |
| WB-001 | 托盘三态与 effective-state | App/Core/Application、托盘状态测试 | .NET CI + Windows 托盘 smoke |
| WB-002 | SVG Logo 与多尺寸 ICO | Assets、XAML、资源契约、生成脚本 | WinUI package + Windows DPI smoke |
| WB-003 | 设置名称与持久化兼容 | Core/App、六语言 catalog、资源测试 | .NET CI + 设置迁移 smoke |
| WB-004 | Installer 统一 process runner | `process_runner.rs`、`main.rs`、`service_plan.rs`、Rust 测试 | fmt/clippy/test + Windows 非提权 process-tree + `RunAs` durable cut-point/VM + Repair 收敛 |
| WB-005 | Probe/Sandbox 发布隔离 | 四个 Probe csproj、Sandbox Cargo、topology tests | architecture test + 最终 payload allowlist |
| WB-006 | 项目地图、执行计划和状态入口 | 本文、生产计划、roadmap、ledger、README | 链接/状态/基线一致性审查 |

若依赖修复与 runner 共用 lockfile，先固定依赖基线，再引入 runner 源码和相应测试；不要用一个不可审查的大提交同时宣称关闭全部 P0。

## 9. 从当前状态到 Core Production Ready

| Gate | 目标与交付物 | 前置依赖 | 退出证据 | 主责 |
|---|---|---|---|---|
| G0 候选准入 | 本文所在 work batch 可审查，恢复候选提交 CI 绿色 | 无 | .NET/Rust 所有 job 绿色；依赖无未处置 high/critical | Release + 各域 owner |
| G1 核心运行正确性 | 有界 runner；P1-01/P1-06/07/08；托盘 Windows smoke | G0 | 双实例、settings generation、proxy/TUN effective state 和 Repair 故障注入全绿 | Application + Runtime + Installer |
| G2 部署与恢复 | .NET runtime/GeoData 输入；对称 durable lifecycle 与证书 ownership | G1，可并行准备输入 | 干净机启动；每个 cut point 后 Repair/Uninstall 收敛 | Installer + Security |
| G3 RC 加固 | 日志安全、P/Invoke、资源生命周期、覆盖率、a11y、运维手册 | G1；与 G2 并行 | 计划内 RC hardening 全部附证据 | Security + QA + 各域 owner |
| G4 候选供应 | 固定工具链、签名/时间戳、SBOM/provenance、不可变 artifact | G2、G3 | 构建并签名精确候选，不公开 promotion | Release |
| G5 产品真实性 | 对 G4 的同一 digest 执行严格 Windows VM 矩阵 | G4 | 无必需 skipped；proxy/TUN/service/reboot/tamper/residue 全部验证 | QA + Runtime |
| G6 受保护发布 | protected tag/environment、attestation、soak、promotion | G5 | 只发布通过矩阵的同一 artifact digest；DoD 全部附证据 | Release |

G0 后可以并行准备 VM/schema 基础设施和非签名 workflow 骨架，但签名候选必须先于 VM E2E，promotion 必须后于该精确候选的 E2E；这消除了“先验证最终签名包、后建立签名流水线”的依赖环。详细任务和最终 Definition of Done 以执行计划为准。

## 10. 需要维护者明确的发布输入

以下决策不能由代码静默假定，最迟在 G2 结束前固定：

1. Authenticode/MSIX 发布者、证书 thumbprint、密钥托管方式和时间戳服务。
2. 隔离 Windows 11 x64 VM provider、UAC/重启编排方式、测试 SID 模型和 runner 保留策略。
3. 版本号与 tag 规则、stable/preview 渠道、最低支持 Windows build、升级兼容窗口。
4. 安全报告渠道、日志/崩溃数据策略、支持边界、P0/P1 缺陷豁免审批人。

在这些输入确定前，可以完成代码与非签名自动化；不能把任何开发证书或个人环境视为正式发布凭据。

## 11. 本轮特殊需求追踪

| 需求 | 实现位置 | 当前状态 | 剩余验收 |
|---|---|---|---|
| 未知/未启用/待命灰色 | `TrayMenuStateBuilder` + `Assets/Tray/Logo.Inactive.*` | 候选完成，E2 | Windows smoke |
| 已验证系统代理绿色 | `TrayMenuStateBuilder` + `Assets/Tray/Logo.SystemProxy.*` | 候选完成，E2 | Windows smoke |
| TUN 使用 C# 紫 | 主色固定为 `#512BD4`，紫色资产与状态优先级 | 候选完成，E2 | Windows smoke |
| 设置项改为“启用托盘色彩状态指示” | 简体中文 catalog；旧持久化 key 保持兼容 | 候选完成，E2 | 设置迁移 smoke |
| Logo SVG 化 | `Assets/Logo.svg`；XAML 和 README 使用 SVG | 候选完成，E2 | WinUI build/package 验证 |

色彩指示关闭时保持固定品牌绿，不表达运行状态；开启后优先级固定为 `已验证 TUN > 已验证系统代理 > 未知/未启用/待命`。TUN checkbox 只表达偏好，图标读取 mutation owner 的最后 verified effective state，未知时 fail-neutral 为灰色。

## 12. 文档维护规则

- 本文回答“项目现在在哪里、各部分由谁负责、证据到哪一级”。
- 生产就绪执行计划回答“下一步按什么依赖顺序关闭哪些 release blocker”。
- 2026-08-10 roadmap 保留全量审计、历史基线和非阻断债务，不再作为当前发布状态摘要。
- stabilization ledger 只在记录 closure commit、reviewer、日期和证据后把架构项标为 `Closed`。
- 每次主分支或发布候选变化时，更新可外部验证的目标 SHA、CI run、E3/E4 证据和 Gate 状态；本文顶部不写自身最终提交 SHA，避免形成不可满足的自引用。不得只勾 checkbox 而不附证据。
