# ClashSharp 详细测绘与发布前审查

- 审查日期：2026-07-18
- 基线分支：`main`
- 基线提交：`0fae2d8`
- 范围：i18n、设置端到端映射、明显 UI/MVVM 问题、运行时一致性、持久化、测试可信度、安装与发布链路、编码规范
- 审查方式：源码静态分析、调用链交叉核对、Debug/Release 构建、.NET/Rust 测试与静态门禁；未启动 WinUI 应用，未修改注册表、系统代理、StartupTask 或 Windows 服务
- 路径约定：未写目录的生产文件均位于 `ClashSharp/ClashSharp/`；其余位置使用仓库相对路径

## 1. 结论

当前代码库可以编译，现有自动化测试全部通过，但**不建议把当前提交直接作为稳定版发布候选**。

未发现已确认的 P0；确认或高置信定位了 11 组 P1，其中最重要的是：

1. 单实例仲裁晚于代理恢复，第二实例有机会关闭第一实例正在使用的系统代理；同一实例内恢复任务也会与启动模式并发写代理状态。
2. `Triggers.json` 非原子写且加载无损坏恢复，一次中断写入即可使后续启动失败。
3. 触发器 UI 保存的流量作用域未被运行时读取；时间/阈值触发可重复、跨事件并发执行；编辑多条件任务会静默丢弃其余条件。
4. 全局重置/清数据、数据包导入以及端口/TUN 修改没有统一的运行态应用事务，持久化值、界面、mihomo、系统代理、StartupTask 和后台服务会发生状态分裂。
5. `Connections` 页面及完整功能实现存在，但 shell、托盘和路由表均无入口，用户完全不可达。
6. 绿色测试程序集重新编译了部分生产源码，却不引用真实应用项目；47 个生产文件及实际装配路径未覆盖，且仓库当前没有 CI。
7. 安装脚本默认生成 `CN=ClashSharp Development` 证书，而包清单 Publisher 是 `CN=linzh`；干净环境默认发布路径不闭环。

架构并非“没有 MVVM”：ViewModel、接口适配器、领域服务和单元测试已经形成了良好基础。但应用级编排仍散落在 `App`、`MainWindow`、`Settings`、`MasterControl` 和静态单例中；页面 code-behind 同时承担 UI、领域对象构造、持久化和系统副作用，属于“局部 MVVM、全局事务缺协调器”的状态。

### 证据等级

| 标记 | 含义 |
|---|---|
| R | 已由命令或测试实际复现 |
| S | 由确定性源码路径直接证明 |
| H | 高置信静态风险，仍需 WinUI/Windows 真机复现时序或视觉表现 |
| L | 审查限制，不作为已确认缺陷 |

### 严重级别

| 级别 | 判定标准 |
|---|---|
| P0 | 数据破坏、权限突破或核心功能普遍不可用，且已确认可立即触发 |
| P1 | 发布前应阻断；可能导致启动失败、系统代理错误、数据丢失、核心功能不可达或运行态与 UI 严重分裂 |
| P2 | 稳定版前应处理；明显功能、可访问性、可靠性、测试或发布链问题 |
| P3 | 架构、维护性、规范和长期质量债务 |

## 2. 验证基线

| 检查 | 结果 | 证据等级 |
|---|---|---:|
| Debug solution build，x64 | 通过，0 warning / 0 error | R |
| Release solution build，x64，单节点、禁用 node reuse/shared compilation | 通过，0 warning / 0 error | R |
| Release .NET tests | 678/678 通过，0 失败，0 跳过 | R |
| `AnalysisLevel=latest-all` build | 通过，0 warning | R |
| Installer Rust tests | 12 个 unit + 5 个 doc tests 通过 | R |
| SandboxTest Rust tests | 11 个 contract/harness tests 通过 | R |
| 两个 Rust crate `cargo fmt --check` | 通过 | R |
| 两个 Rust crate `cargo clippy --all-targets --locked -- -D warnings` | 通过 | R |
| NuGet vulnerable scan | 当前源下未发现已知漏洞 | R |
| `dotnet format --verify-no-changes` | 失败，exit 2；约 30,954 条诊断，主要为行尾 | R |
| 强制 XML 文档警告 `CS1591` | 失败，160 条诊断 | R |
| WinUI 真机启动/视觉/键盘/UIA | 未执行 | L |
| Windows Sandbox 全场景 | 未执行；现有 `all` 契约允许 3/4 默认场景 skipped 后仍成功 | L/S |
| RustSec `cargo audit` | 环境未安装，仓库也无对应门禁 | L |

Release 构建环境使用 .NET 10 preview SDK，命令输出了 `NETSDK1057` 信息但不是 warning。该构建证明当前源码/XAML可编译，不证明目标用户环境、注册表、系统代理和安装签名链路正确。

## 3. 系统测绘

### 3.1 主要运行链

```text
App.OnLaunched
  ├─ AppSettingsAuditLogService.Start
  ├─ TriggerService.Start
  ├─ 创建并激活 MainWindow
  ├─ Task.Run(ProxyRecoveryService.Recover)
  └─ ConnectionSamplingService.Start

MainWindow.Loaded
  ├─ 单实例检查
  ├─ 启动触发器上下文创建/评估
  ├─ 启动冲突与引导
  └─ NetworkTakeoverService.ApplyMode
```

该次序直接解释了 P1-01：单实例检查之前已经启动了会读写共享 LocalData、SQLite 和系统代理的服务；代理恢复又与 `ApplyMode` 并发。

### 3.2 层次与当前责任

| 层 | 已有良好基础 | 主要越界 |
|---|---|---|
| XAML/View | 大量 Binding，组件化 SettingRow、MasterInfoTile 等 | 自定义控件缺键盘/UIA语义；页面直接构造领域对象和执行系统操作 |
| Page ViewModel | `ObservableObject`、Command、接口适配器、较多单测 | `SettingsViewModel` 和 `MasterControlViewModel` 过大；部分异步副作用 fire-and-forget |
| Application coordinator | 尚无统一实现 | 启动、关闭、单实例、托盘、设置快照应用和路由散落在多个 code-behind |
| Domain/service | 网络、配置、日志、触发器等服务边界基本清晰 | 广泛使用 `.Instance`；若干服务持久化/异步生命周期契约不完整 |
| OS/mihomo/storage | 有锁、日志、路径检查、SQLite backup 等防护 | 跨服务/跨进程事务没有共同锁；后台生产者缺少可等待的 quiesce |

### 3.3 静态规模

- 生产 C#：153 个文件，约 44k 行；测试 C#：62 个文件。
- 最大生产文件：`LocalizationResources.cs` 3917 行、`SettingsViewModel.cs` 2701 行、`MasterControlViewModel.cs` 1585 行、`LogStorageService.cs` 1141 行。
- 关键 code-behind：`Settings.xaml.cs` 1090 行、`Triggers.xaml.cs` 659 行、`MainWindow.xaml.cs` 612 行、`MasterControl.xaml.cs` 449 行。
- 生产源码中约 375 处服务 `.Instance` 引用；`Settings.xaml.cs` 一页直接组装约 64 处 singleton 引用。
- 20 个 XAML 源文件中约 420 处 `{Binding}`，没有使用 `x:Bind`。静态比对未发现 Binding 根属性或事件处理器方法缺失。

规模本身不是缺陷；问题在于这些大文件同时承担了跨层事务，导致同一设置在“保存、显示、应用、导入、重置、关闭”不同路径上行为不一致。

## 4. P1：发布阻断项

### P1-01 单实例仲裁晚于代理恢复，代理状态存在跨进程及同进程竞态（S/H）

证据：

- `ClashSharp/ClashSharp/App.xaml.cs:52-57` 在窗口激活前后启动审计、触发器、后台代理恢复和采样。
- 单实例检查直到 `ClashSharp/ClashSharp/MainWindow.xaml.cs:244-250,285-322` 执行。
- `ProxyRecoveryService.cs:66-98` 只按 loopback/端口判断“陈旧代理”并禁用，不能识别该端口是否由第一实例持有。
- `MainWindow.xaml.cs:271-277` 与 `NetworkTakeoverService.cs:198-206` 同时可能启用代理；`WindowsProxyService` 的锁只覆盖单进程内单次写入。

影响：第二实例即使最终退出，也可能先关闭第一实例的有效系统代理；单一实例中恢复任务与启动模式应用也可能形成“最终写入者获胜”，使 `CurrentMode`、mihomo 和 Windows 代理不一致。

修复方向：把单实例仲裁放到所有共享资源和窗口初始化之前；将恢复、启动模式、关闭恢复纳入一个串行的 `NetworkStateCoordinator`，并通过可取消事务/代次号避免旧任务晚写。

回归门禁：两个进程 + fake registry/barrier 的确定性测试；首实例处于 RuleTakeover 时启动第二实例并退出，断言代理值不变。

### P1-02 `Triggers.json` 非原子写，损坏文件可阻断后续启动（S）

- `TriggerService.cs:36-40,97-98` 的生产 singleton 构造时立即 `Load()`；`App.xaml.cs:53` 启动时无保护访问。
- `TriggerService.cs:555-574` 对 JSON/IO 异常没有恢复；`:577-586` 直接 `File.WriteAllText`，会先截断原文件。
- 对照 `ProfileCatalogService.cs:585-618` 已有损坏 JSON/IO 回退逻辑。

影响：掉电、磁盘满、进程终止或手工损坏后，静态初始化异常可中断应用启动，并丢失全部触发器。

修复方向：临时文件写入、flush、同卷原子替换；保留最后有效备份；加载时隔离损坏文件并返回可诊断默认集合。

回归门禁：`Triggers.json` 内容为 `{`、无读权限、写中断、磁盘异常四类测试；应用仍能启动且原数据可恢复。

### P1-03 触发器作用域未实现，时间/阈值动作可重复或并发执行（S/H）

- `View/Triggers.xaml.cs:425-448,536-564` 展示并保存 `Scheduled`、`Startup`、`Cumulative`。
- `TriggerService.cs:529-547` 对 `TotalTraffic`/`TrafficInWindow` 不读取 `condition.Value`；工厂只生成累计值和固定五分钟窗口。
- `SystemTime` 条件使用 `now >= target`，一旦到点永久为真。
- 冷却仅对 `Periodic` 事件生效（`TriggerService.cs:521-527`）；非 periodic 事件可立即重触发。
- periodic 与 runtime drain 有各自的并发标志，但没有任务级共同执行门；二者可同时读取旧 `LastTriggeredAt` 并执行同一动作。

影响：作用域设置属于无效 UI；达到时间/阈值后，切换模式、关闭连接、退出应用、通知等动作可能每五分钟或并发重复。

修复方向：先定义精确语义（边沿触发、每日一次、会话一次、累计/窗口）；让 `Matches` 返回可提交的触发令牌，在单一串行执行器内原子更新 `LastTriggeredAt` 后执行动作。

回归门禁：固定时钟、三类 scope、跨午夜、periodic/runtime barrier、失败重试和幂等性测试。

### P1-04 编辑多条件触发器会静默丢失数据（S）

- `Triggers.xaml.cs:156` 编辑时只读取 `Conditions.FirstOrDefault()`。
- `Triggers.xaml.cs:216-240` 保存时始终用单元素 `[condition]` 替换条件集合。
- 模型、normalizer 和持久化层均允许多个条件。

影响：打开并保存已有多条件任务，即使用户没有删除条件，其余条件也会被永久丢弃。

修复方向：要么完整实现多条件编辑器，要么在模型/导入层禁止多条件并拒绝打开；不能静默降级。

回归门禁：构造两个条件的任务，打开后不改动直接保存，断言结构完全保留。

### P1-05 触发上下文同步等待 HTTP，启动/UI 发布线程可冻结并逸出异常（S/H）

- `TriggerEvaluationContextFactory.cs:44-53` 对异步 HTTP 使用 `.GetAwaiter().GetResult()`。
- `MihomoControllerClient.cs:34-37` 超时为五秒。
- `MainWindow.xaml.cs:231-255` 在 UI 启动路径同步创建上下文；即使触发器最终禁用，调用实参也已经求值。
- runtime event hub 同步发布，drain 在第一次 `await` 之前同样创建上下文。
- 工厂没有兜底 `JsonException`、SQLite/IO 等异常，外层入口又是 `async void`。

影响：core 无响应时窗口可冻结约五秒；非法 controller JSON 或数据库异常可能成为未处理 UI 异常。

修复方向：`CreateAsync(CancellationToken)`；触发器关闭时在创建上下文前短路；只在后台获取昂贵数据并把预期失败转为显式 degraded result；UI 入口设顶层异常边界。

### P1-06 全局重置/清数据没有同步运行态，且会抹掉 restart baseline（S）

- UI 入口：`Settings.xaml.cs:1020-1056`。
- `SettingsViewModel.cs:2653-2689` 维护后只执行 `Load()` 和属性通知。
- `Load()` 在 `:1458-1507` 把语言、托盘等 `_loaded*` 基线直接改为新值。
- 全局路径没有执行构造时注入的主题、StartupTask、采样等回调；各分组重置 `:2523-2651` 反而会执行。
- `RuntimeShutdownService.cs:89-99` 没有停止 TriggerService 或审计订阅；`ConnectionSamplingService.Stop()` 不等待在途任务。
- `AppDataMaintenanceService.cs:98-107` 随即删除数据，后台服务可能重新创建文件。

影响：UI 显示默认值，但主题、Windows 启动项、采样、mihomo/代理仍保持旧状态；语言/托盘的重启提示可能错误消失；“清除全部数据”后数据可能再次出现。

修复方向：引入可等待的 `QuiesceAsync`，先停止所有生产者，再维护数据；随后由统一 `ApplySettingsSnapshotAsync` 应用新快照。只有实际应用完成或明确记录待重启项后才能更新 baseline。

### P1-07 数据包导入只重放部分副作用，存储、缓存与运行态分裂（S）

- `ClashDataPackageService.cs:101-131,356-373` 可覆盖模式、活动配置、TUN、端口、ProfileCatalog 和 mihomo 文件。
- `Settings.xaml.cs:898-907` 导入后只应用语言、主题、强调色、StartupTask、采样，然后 `Load()`。
- 未重新应用 `CurrentMode`、活动 profile、透明代理、端口、核心配置、系统代理或 tray；也未失效 `ProfileCatalogService` 缓存。
- `ProfileCatalogService.cs:64-70,397-402,587-603` 明确缓存文档。
- 31 个 `KnownKeys` 中，数据包描述器有 29 个；其中 `MainlandChinaDisplayEnabled` 是旧兼容键，当前实际遗漏的是用户可配置的 `MasterHeroStatusLayout`。
- rollback 清理/恢复中的部分 IO 异常被吞掉，存在无声部分回滚风险。

影响：导入报告成功且页面显示新值，但当前 mihomo、系统代理、profile 列表和托盘仍使用旧状态；Hero 布局不能迁移。

修复方向：数据包只负责解析/暂存；提交交给应用级 settings transaction，明确区分“即时应用”和“必须重启”，统一失效缓存、重启 core/代理并验证最终状态。导入清单应由设置元数据生成，避免手工漏项。

### P1-08 TUN/混合端口修改只保存不应用，且 UI 立即宣称新状态（S）

- `SettingsViewModel.cs:1919-1929` 的 TUN setter 只写设置与属性。
- `:1986-2002` 的端口 setter 只写设置，随后立即刷新代理信息。
- 唯一 `SettingChanged` 订阅者是审计日志；当前 core/系统代理不会被重新配置。
- 实际 TUN/端口只在 `NetworkTakeoverService.cs:176-217` 下次 `ApplyMode` 时读取。
- `ProxyRecoveryService` 却会立刻读取新端口判断 stale proxy。

影响：UI/诊断认为端口已经改变，mihomo 和 Windows 代理仍监听旧端口；随后 stale recovery 还可能基于错误端口关闭代理。

修复方向：选择并固定一个产品语义：即时事务性重启 core/重写系统代理，或显式“下次启动生效”并保留 restart-required；不能立即显示为已应用。

### P1-09 `Connections` 完整功能没有任何可达入口（S）

- `View/Connections.xaml` 与 `ConnectionsViewModel.cs` 均存在完整实现。
- `MainWindow.xaml:45-110` 没有 Connections 导航项。
- `MainWindow.xaml.cs:482-496` 路由表和 `TrayMenuStateBuilder.cs:188-202` 托盘页面表均没有该页。
- 全库 shell 路径没有 `typeof(Connections)` 或 `Tag="Connections"`，但六种语言仍保留 `Nav.Connections`。

影响：连接监控、单条关闭、全部关闭、快照持久化整组功能对用户不可达。

修复方向：若功能受支持，把页面加入唯一的路由元数据源并由该元数据生成 shell/托盘；若已弃用，删除页面、资源和误导测试。

回归门禁：每个 Page 必须有路由或显式 `IntentionallyUnrouted` 标记的契约测试。

### P1-10 干净环境默认 MSIX 签名主体与清单 Publisher 不匹配（S）

- `ClashSharp/ClashSharp/Package.appxmanifest:14`：`Publisher="CN=linzh"`。
- `ClashSharp/Installer/build.ps1:10-14` 默认生成/查找：`CN=ClashSharp Development`。
- 脚本遇到已有 PFX/CER 时不验证其 Subject 是否与 manifest 一致。
- Microsoft 的 MSIX 文档明确要求签名证书 Subject 与 manifest Publisher 精确匹配，否则签名失败：<https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing>。

影响：没有本机遗留证书的干净 checkout 无法按默认脚本稳定生成可安装包；有遗留证书时结果依赖机器状态。

修复方向：从 manifest 读取唯一 Publisher；生成、选择和复用证书时校验 Subject、EKU 和 thumbprint；把开发签名与正式分发签名配置分离，在干净 CI runner 上执行 package/sign/install/uninstall smoke。

### P1-11 绿色测试不覆盖真实生产装配，且当前无自动 CI（S/R）

- `ClashSharp.Tests.csproj:11,35-142` 定义 `UNIT_TESTS` 并源码链接生产文件，而不是引用应用项目。
- 153 个生产 C# 文件只链接 106 个，遗漏 47 个，包括 `App.xaml.cs`、`MainWindow.xaml.cs`、所有页面 code-behind 和多项 factory/wiring。
- `TriggerService`、`RuntimeTrafficRateService` 等存在 `UNIT_TESTS` 条件分支，测试对象与生产对象并不完全相同。
- 当前仓库没有 `.github` workflow；原 CI 在提交 `7c9c5fd` 被删除。
- 现有 678 个用例全绿，但本报告中的启动顺序、路由缺失、导入应用、窗口关闭和系统副作用问题均能同时存在。

影响：“测试全绿”目前主要证明隔离逻辑和源码契约，不证明真实 app 项目、WinUI 装配和启动事务。

修复方向：恢复 required CI；保留纯单测的同时，增加生产项目引用/独立 integration host、应用装配 smoke、XAML 路由契约和 Windows Sandbox smoke。禁止默认场景 skipped 后仍报告全通过。

## 5. P2：稳定版前应处理

### 5.1 i18n 完整性与一致性

#### P2-I18N-01 英文 fallback 掩盖真实缺译（S）

`LocalizationResources.BuildTranslations` 在 `LocalizationResources.cs:3895` 对所有语言调用 `FillMissingTranslations(..., English)`。现有 `Translations_NonEnglishLanguages_MatchEnglishKeySet` 测试检查的是补全后的字典，因此不能证明译文完整。

| 语言 | 显式键数 | 相对英文缺失 | 98 个动态所需键中缺失 |
|---|---:|---:|---:|
| English | 665 | 0 | 0 |
| 简体中文 | 665 | 0 | 0 |
| 繁体中文 | 631 | 34 | 14 |
| Français | 591 | 74 | 36 |
| Русский | 591 | 74 | 36 |
| Deutsch | 591 | 74 | 36 |

繁中主要缺 `Master.Hero`；法/俄/德还缺触发器条件/动作、Master tile、单实例对话框及应用生命周期等。运行时安全 fallback 是合理的，但发布完整性测试必须在 fallback 之前检查显式资源，或维护经过评审的 allowlist。

正向结果：显式翻译中未发现重复键、空值或复合格式占位符签名不一致；英文和简中键集合完整；字面量 `GetString` 键未发现不存在项。

#### P2-I18N-02 语言切换只改资源，不改格式 culture（H）

`LocalizationService` 没有同步 `CurrentCulture/CurrentUICulture`；多个 VM 使用线程 `CurrentCulture` 格式化数字和日期。中文系统切换法语/德语后，文案与数字/日期格式可能混用两种区域约定。需要明确产品设计并增加 culture matrix 测试。

#### P2-I18N-03 导入语言后 shell 与当前 Settings 页面可混语（S/H）

shell 订阅 `LanguageChanged`，Settings VM 没有；导入调用全局语言后只 `Load()`，没有 `RaiseLocalizedTextChanges`。若语言设计为重启生效，则导入不应即时切换；若支持即时切换，所有活动 VM 必须统一刷新。

#### P2-I18N-04 翻译语义漂移与底层异常直出（S）

- `Settings.TransparentProxy.Description` 的中/繁/英描述“服务未部署前不可用”，俄/法/德却描述“回退系统代理”，与实际禁用行为不同。
- `Settings.xaml.cs:1075-1087` 和连接测试直接向 UI 拼接 `exception.Message`，会混入系统语言、英文或本地路径。

### 5.2 设置、运行态与可靠性

#### P2-SET-01 StartupTask 偏好先保存，拒绝/Other 状态不回滚（S）

`SetLaunchAtStartupEnabled` 先持久化，再 fire-and-forget 同步；`StartupLaunchService` 对 `Other` 静默返回，对常见异常仅记录日志且不核验最终状态。用户开关可以显示开启而 Windows 实际关闭。动作应返回实际状态，成功后提交偏好，失败回滚并反馈。

#### P2-SET-02 数据包枚举接受未定义数字（S）

`ClashDataPackageService.cs:536-543` 只使用 `Enum.Parse<T>`，`999` 可验证成功并持久化；`AppSettingsService` 读取时又把未定义值静默当默认，破坏“导入成功”的契约。需同时使用 `Enum.IsDefined` 并在任何写入前拒绝整个包。

#### P2-SET-03 连接测试 URL 的失败反馈和默认值错误（S）

- `Settings.xaml.cs:371-380` 忽略 `SetConnectionTestUrls` 的 false 返回，对话框直接关闭。
- `AppSettingsService` 三个目标默认 URL 不同，但空值归一化共用 `DefaultConnectionTestUrl`，Proxy2/Direct 会回到错误目标。

#### P2-RUN-01 采样存储异常会静默终止循环（S）

`ConnectionSamplingService.cs:204-255` 的循环不保护 `SampleOnceAsync`；SQLite/IO 写入异常可 fault `_samplingTask`，任务无人观察、无重启，统计和流量触发器从此使用陈旧数据。

#### P2-RUN-02 `sc.exe` 超时/取消语义失效（S/H）

`MihomoServiceManagerFactory.cs:78-94` 先同步 `ReadToEnd()` 再 `WaitForExit(5000)`，前者已经可以无限等待；也未检查 `WaitForExit` 返回值。提权路径取消只返回 `-1`，没有终止已启动进程。应使用真正的异步读取、取消时杀进程树、随后重新查询 SCM 最终状态。

#### P2-RUN-03 异步命令与乐观 UI 缺少统一错误通道（S）

`AsyncRelayCommand.Execute` 丢弃任务且没有 error sink；`MasterControlViewModel` 先改设置再丢弃 dispatcher task；Settings 也丢弃 StartupTask 同步。意外异常可变成未观察 fault，外部操作失败后 UI 仍显示成功。命令需要统一异常策略、busy/error 状态和提交/回滚顺序。

#### P2-RUN-04 core 诊断缓冲区并发写非线程安全 `StringBuilder`（S/H）

`MihomoCoreService.cs:167-181,248-260` 的 stdout/stderr 回调并发写同一个 `StringBuilder`，主线程还可能同时 `ToString()`。启动失败路径可能得到损坏诊断或异常；应改为线程安全 channel/lock 并做并发输出压力测试。

### 5.3 UI、可访问性与交互

#### P2-UI-01 长列表视口/滚动结构高风险（H）

Profiles、Links、Rules、Proxies、Connections 的主列表位于纵向 `StackPanel` 中且没有可靠的有限高度/外层滚动。大量项目时内部 ListView 难以形成虚拟化视口，后续区域可能排到页面外。改用 `Grid Auto/*` 或明确的单一 ScrollViewer，并在 800×600、200 项数据下验证最后一项可达。

#### P2-UI-02 代理选择失败/快速切换可显示未应用值（H）

Proxies ComboBox 的选择由 `async void SelectionChanged` 直接提交，无忙状态、串行化和失败回滚。失败时 UI 可显示 B 而 mihomo 仍是 A；A→B→C 快速选择也可能由较慢旧请求覆盖新请求。

#### P2-UI-03 MasterInfoTile 主要动作只支持指针（S）

交互根是带 `Tapped` 的普通 Grid，仿 ToggleSwitch 只是 Border；没有 Enter/Space、TogglePattern、可访问名称或状态。应基于 `Button/ToggleButton` 实现并绑定 Command/CanExecute。

#### P2-UI-04 任意亮色强调色会产生确定性低对比（S）

ColorPicker 允许任意 RGB，主题服务对强调色固定使用白色文字；白色为 1:1，黄色约 1.07:1。应按相对亮度选择黑/白前景，并覆盖 hover/pressed/checked 和高对比模式。

#### P2-UI-05 至少 10 个纯字形按钮缺可访问名称（S/H）

Connections 关闭、Logs 返回、Proxies provider 刷新，以及 Triggers 的编辑/上移/下移/删除/返回/条件选择/动作选择缺少本地化 `AutomationProperties.Name`，多处也无 Tooltip。需加入对象上下文名称并用 Accessibility Insights 真机验证。

#### P2-UI-06 自定义 Overlay 不具备完整模态语义（S/H）

`CenteredDialogOverlay` 只消费 pointer input 并把焦点放到关闭按钮；没有焦点循环、Esc、Dialog UIA peer、底层 inert 或关闭后焦点恢复。优先使用 `ContentDialog`，否则完整实现模态契约。

#### P2-UI-07 导航选中项与实际页面可失步（S）

Master 的导入/导出 tile 直接 `Frame.Navigate(Settings)`，不更新 NavigationView 选中项，也不跳到备份区域；托盘 Logs 路由在 shell 映射为 null，会保留旧高亮。所有跳转应经过一个支持 `tag + anchor/action` 的 NavigationService。

#### P2-UI-08 关闭确认对话框可能重入（H）

`OnAppWindowClosing` 是 `async void`，每次事件都创建并 `ShowAsync` 新对话框，无 `_isClosePromptOpen` 或在途 Task guard。连续 Alt+F4 可能在同一 XamlRoot 并发弹窗并逸出异常。

#### P2-UI-09 Connections 行布局小于自身声明最小窗口（S/H）

行内固定列与间距至少约 754 DIP，页面 padding 后至少 818 DIP，尚未计算 NavigationView；窗口最小宽度是 800 DIP。恢复路由前必须加入自适应断点、堆叠模板或横向滚动。

### 5.4 发布、供应链与测试可信度

#### P2-REL-01 安装器信任锚和下载完整性未闭环（S/H）

- 安装器把开发签名证书导入 `CurrentUser\TrustedPeople`，卸载没有按 thumbprint 删除。
- `build.ps1` 默认下载 `latest` mihomo，SHA 可为空；更新脚本仅在调用者提供哈希时校验，随后执行下载的 exe。

正式链路应固定版本与 SHA/签名来源，生成 SBOM/provenance；开发证书必须精确追踪并在卸载时安全移除。

#### P2-REL-02 Sandbox 的 `all` 可在 3/4 默认场景跳过后成功（S）

默认场景包括 install-only、launch-no-proxy、startup-with-proxy-config、cleanup-uninstall；guest 脚本后三项直接写 skipped，host 和 Rust validator 都把 skipped 当成功。当前 11 个 Rust 测试只证明 harness 契约，不证明 Windows 场景实际运行。

#### P2-REL-03 版本与构建入口漂移（S）

版本散落在 package manifest、app.manifest、Python 构建工具、VM 显示 fallback、Installer Cargo 等位置；`Tools/build_installer.py` 仍生成旧式 loose publish zip，与 README 所述 Rust installer/MSIX 路径不一致。应建立单一版本源并删除或明确标记旧入口。

#### P2-QA-01 格式和规范门禁当前不可执行（R/S）

`.editorconfig` 强制 CRLF，但仓库无 `.gitattributes`，生产 C# 同时存在 LF、mixed、CRLF；`dotnet format --verify-no-changes` 失败。`CodingStyle.md` 又要求 XML 文档零遗漏为 build-breaking，但 csproj 未启用该门禁，强制检查得到 160 个 `CS1591`。需先决定规范，再用 `.gitattributes`、analyzer severity 和 CI 固化。

#### P3-QA-02 依赖无当前已知 NuGet 漏洞，但版本治理仍是手工作业（R）

- `Microsoft.Extensions.Hosting.WindowsServices`、`Microsoft.Data.Sqlite`、`System.Drawing.Common` 仍为 `10.0.0`，当前可用同系列修订版为 `10.0.10`。
- Windows SDK BuildTools 有修订版更新；Test SDK、coverlet 和 xUnit runner 也有更新。
- `xunit 2.9.3` 在 NuGet 查询中标为 legacy，并给出 xUnit v3 替代；迁移需单独评估，不应与缺陷修复混在一次大升级中。
- Windows App SDK 查询跨越主版本，不能仅凭 `outdated` 输出直接升级；应先建立 WinUI/打包/最低系统版本兼容矩阵。

建议启用定期依赖 PR、NuGet/RustSec 审计和 lock/provenance 记录，把安全修订与主版本迁移分开处理。

## 6. 设置端到端映射

`AppSettingsService.KnownKeys` 有 31 个键；其中 `MainlandChinaDisplayEnabled` 是兼容旧键，当前设置模型约 30 项。所有 Settings 页面可见持久化项都能追到 `ISettingsStore`/`AppSettingsService`，基础端口、采样间隔、颜色和 URL 校验已经存在。主要问题不在“有没有保存”，而在“保存后由谁、何时、以什么事务应用”。

| 设置组 | 保存路径 | 运行时消费者 | 普通修改 | 导入后 | 全局重置后 | 结论 |
|---|---|---|---|---|---|---|
| 语言 | Settings VM → AppSettings | LocalizationService / active VM | 设计为重启提示 | 部分即时切换 | baseline 被重置 | 不一致 |
| 主题 | Settings VM → AppSettings | AppThemeService | 即时应用 | 即时应用 | 不重新应用 | 全局路径缺失 |
| 强调色 | Settings VM → AppSettings | AppThemeService | 重启提示/资源 | 导入即时应用 | baseline 风险 | 语义不统一 |
| StartupTask | Settings VM → AppSettings | StartupLaunchService | fire-and-forget | fire-and-forget | 不重新同步 | 外部状态不可确认 |
| CurrentMode | AppSettings | NetworkTakeoverService | 由模式动作应用 | 不应用 | 不应用 | 高风险 |
| ActiveProfileId | AppSettings/ProfileCatalog | core config | 相关页面动作应用 | 缓存不失效 | 缓存仅清数据路径部分处理 | 高风险 |
| TUN | Settings VM → AppSettings | NetworkTakeoverService | 只保存 | 不应用 | 不应用 | 高风险 |
| MixedPort | Settings VM → AppSettings | core config/system proxy | 只保存、UI立即刷新 | 不应用 | 不应用 | 高风险 |
| 采样启用/间隔 | Settings VM → AppSettings | ConnectionSamplingService | 重启采样 | 重启采样 | 清数据后显示默认开启但服务已停 | 不一致 |
| 退出恢复/启动 stale check | AppSettings | MainWindow/ProxyRecovery | 下次对应生命周期读取 | 存储生效 | 存储生效 | 可接受但应标明时机 |
| 冲突检查/启动行为/引导 | AppSettings | MainWindow startup | 下次启动读取 | 存储生效 | 存储生效 | 可接受 |
| Trigger/通知 | AppSettings getter | TriggerService/NotificationService | 实时读取 | 基本实时 | 后台生命周期仍有缺口 | 逻辑链较完整 |
| 关闭行为 | AppSettings getter | MainWindow closing | 实时读取 | 实时读取 | 实时读取 | 完整 |
| 托盘图标/菜单 | AppSettings | SystemTrayService | 部分需重启 | 不重建 | baseline 被重置 | 不一致 |
| 大陆功能 | AppSettings getter | Region/Mainland service | 实时读取 | 实时读取 | 实时读取 | 较完整 |
| 连接测试 URL | AppSettings | 诊断/测试 | 保存；错误提示缺失 | 可导入 | 可重置 | 默认值 bug |
| Hero 布局 | AppSettings | MasterHeroStatusLayoutService | 即时保存 | **未包含** | 可重置 | 数据包漏项 |

建议把设置元数据提升为单一事实源，每项声明：键、类型、默认值、校验、导入/导出、即时应用 handler、是否需重启、缓存失效、回滚策略。由元数据生成 package descriptors 和测试矩阵，避免五套手工映射漂移。

## 7. MVVM 规范结论

### 符合点

- Model 基本不依赖 UI 或服务 singleton；历史上的 `ActiveConnection` 模型污染已清理。
- 多个 VM 通过小接口/adapter 隔离服务，单元测试可替换依赖。
- XAML Binding 根路径和事件处理器静态检查未发现断裂。
- 网络配置、Profile、日志、通知、触发器等领域能力已拆为服务，而非全部塞入窗口。

### 不符合或偏离最佳实践的重点

1. **View 承担领域编辑器。** `Triggers.xaml.cs` 保存编辑状态、构造/修改 `TriggerTask`、做单位换算和验证。
2. **Shell 承担应用事务。** `MainWindow.xaml.cs` 同时管理 shell、WndProc、托盘、单实例、启动检查、代理接管和关闭事务。
3. **Settings View 是 composition root + use case coordinator。** 它直接引用大量 singleton，并负责导入、导出、维护、诊断、主题和外部状态重放。
4. **静态 singleton 是默认依赖获取方式。** 这隐藏生命周期和跨服务次序，直接促成代理恢复、清数据和导入问题。
5. **异步命令契约不完整。** `async void`/fire-and-forget 没有一致的错误、取消、忙状态和重入策略。

### P3 架构与规范债务

- `LogStorageService.cs` 以单一 `_syncLock` 串行大量读取、写入、清理、导出和维护；VACUUM/维护有机会阻塞采样与触发上下文读取。应先测量锁等待，再按只读查询、写入队列和维护窗口拆分。
- `CodingStyle.md` 要求每个文件显式 `#nullable enable`，但 153 个生产 C# 中仅 26 个显式声明；项目级 Nullable 已启用，所以这不是当前空引用功能缺陷，而是规范与实现不一致。
- 文件头日期、XML 文档和 analyzer severity 主要依赖人工约定；`.editorconfig` 多数规则只是 suggestion，普通 build 不能执行文档所称的 build-breaking 政策。
- `LocalizationResources.cs`、两个大型 VM、Settings/Triggers code-behind 已达到单文件高变更冲突区；应按资源域和 use case 拆分，而不是仅按行数机械切文件。
- 历史架构审查清单与 CI 一起从当前树删除，使已知债务没有可见 owner、验收条件和回归状态。报告应进入版本控制，并由 issue/CI 继续追踪。

建议目标边界：

```text
View/XAML
  └─ PageViewModel（状态、命令、验证、busy/error）
       └─ Application Use Case / Coordinator
            ├─ SettingsSnapshotCoordinator
            ├─ NetworkStateCoordinator
            ├─ StartupCoordinator
            ├─ Shutdown/DataMaintenanceCoordinator
            └─ NavigationService
                 └─ Domain services / repositories / OS adapters
```

不要一次性重写所有页面。先用本次 P1 对应的应用事务抽出 coordinator；只有当调用点已经迁移并有集成测试时，再消除 singleton 和瘦身 code-behind。

## 8. 测试可信度分析

- 62 个测试文件、363 个 `[Fact]/[Theory]` 声明，理论数据展开后 678 个 case。
- `AppResourcePackagingTests.cs` 单文件 2206 行、96 个 Fact，大量使用源码文本/顺序断言；能保护资源和 wiring 文本，但不能代替运行时行为。
- 测试全局关闭 xUnit 并行，无法自然暴露本次发现的竞争条件。
- production/source-link 差异和 `UNIT_TESTS` 条件分支使测试更像“复制编译的逻辑单测”，不是 app assembly 测试。
- 已有重要正向回归：连接流量正 delta、通知触发防自循环、五分钟窗口、WAL 安全导出、TriggersEnabled 动态语义、纯净 ActiveConnection 模型。

建议测试金字塔：

1. 继续保留快速纯逻辑单测。
2. 新增生产 assembly/DI 装配测试，禁止遗漏 factory、App、MainWindow 路由元数据。
3. 对设置应用、导入、reset、shutdown、trigger execution 建立无 WinUI 的 coordinator integration tests。
4. 少量 WinUI smoke 覆盖路由、Binding Diagnostics、800×600、键盘/UIA、对话框重入。
5. Windows Sandbox 覆盖 package install、首次启动、代理模式、startup config、卸载清理；默认场景 skipped 必须使门禁失败。

## 9. 安全候选项（需真机确认，不计入已确认 P1）

`MihomoServiceManager` 创建服务时未显式指定 `obj=`；Windows 默认通常会使用高权限账户。服务参数中的 config/workdir 来自用户 LocalData，worker 只验证路径存在便启动 mihomo。

如果普通用户可修改这些文件，而服务以 LocalSystem 读取执行，就形成“高权限服务消费低权限可写输入”的完整性边界。需要在标准用户真机上执行：

- `sc qc ClashSharpMihomo` 确认实际身份；
- `icacls` 确认 service binary、config、workdir 和父目录 ACL；
- 服务停止/启动前后尝试篡改配置与可执行路径；
- 确认 service SID、受限账户或管理员所有目录策略。

未完成上述验证前，不把它描述为已证实漏洞；但正式发布前必须关闭该问题。

## 10. 建议修复顺序

### 阶段 A：先建立能失败的门禁

1. 恢复 CI；固定 x64 Debug/Release build、678 tests、Rust fmt/clippy/test、NuGet/RustSec 审计。
2. 先写 P1-01 至 P1-08 的失败回归测试，特别是并发 barrier、损坏 JSON、全局 settings snapshot 和清数据 quiesce。
3. 修复 `.gitattributes`/行尾后再启用 `dotnet format` required gate；明确 XML 文档政策。

### 阶段 B：运行安全与数据完整性

1. 单实例前置；串行 NetworkStateCoordinator。
2. Trigger 原子持久化、损坏恢复、任务级串行/幂等与明确定义的 scope。
3. 全异步 TriggerEvaluationContext；统一异常边界。
4. 可等待 shutdown/quiesce，保证清数据和关闭时没有后台写入者。

### 阶段 C：设置事务

1. 建立 SettingsSnapshotCoordinator。
2. 统一普通修改、导入、分组 reset、全局 reset、清数据后的应用路径。
3. 为端口/TUN、StartupTask、tray、语言等定义即时/重启语义和最终状态验证。
4. 由设置元数据生成导入/导出、校验和测试矩阵。

### 阶段 D：UI/MVVM

1. 决定 Connections 是恢复路由还是删除。
2. 修复列表视口、代理选择提交/回滚、关闭重入、统一 NavigationService。
3. 使用标准 Button/ToggleButton/ContentDialog，完成键盘、UIA 和颜色对比。
4. 逐步把 Trigger editor 和 Settings use cases 从 code-behind 移到 VM/coordinator。

### 阶段 E：发布链

1. 清单 Publisher 作为签名 Subject 唯一来源；干净 runner package/sign/install/uninstall。
2. 固定 mihomo 版本、SHA/签名和 provenance；卸载清理开发证书。
3. 单一版本源；删除或隔离旧 Python installer 入口。
4. Sandbox 默认场景必须真实执行，不允许 silent skipped。

## 11. 审查限制

- 没有启动桌面应用，以避免修改系统代理、HKCU、StartupTask、Windows 服务或与用户现有实例冲突。
- 因而长列表、800×600、高 DPI、焦点/UIA、对话框重入、代理请求乱序属于高置信静态风险，仍需真机动态确认。
- 没有在全新 VM 上构建/签名/安装 MSIX；证书主体不匹配由源码和 Microsoft 规范直接确认，实际错误消息待 clean runner 记录。
- 没有执行真实 Windows Sandbox 场景；仅审查了 harness 代码并运行 Rust contract tests。
- 没有母语级审校俄/法/德译文，只检查键、fallback、占位符和明显语义漂移。
- 未安装 `cargo audit`，所以不能声称 Rust 依赖无已知漏洞。

## 12. 最终放行条件

至少满足以下条件后，才建议进入稳定版候选：

- P1-01 至 P1-10 全部有可复现测试并修复；P1-11 的 CI/生产装配门禁恢复。
- 全局 reset、clear data、import、端口/TUN、StartupTask 的 UI/持久化/运行态矩阵全部一致。
- 触发器损坏恢复、作用域、每日/会话一次语义和并发幂等通过测试。
- Connections 明确恢复或删除；键盘/UIA/对比度完成一次真机检查。
- Debug/Release build、.NET tests、Rust fmt/clippy/test、format、依赖审计全部由 clean CI 通过。
- clean VM 完成 MSIX package、签名、安装、首次启动、代理 smoke、卸载与证书清理。
