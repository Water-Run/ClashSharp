# ClashSharp 项目结构、MVVM、编码质量与 Installer 总审查

日期：2026-08-31（Asia/Shanghai）<br>
范围：当前 `main` 工作树中的主体、唯一 C# / WPF Installer、内部运行组件、测试与打包脚本<br>
定位：本文件是当前源码状态的汇总入口；旧审查文档继续保留历史证据，但其中 Rust/Slint 双轨、旧项目数量和旧测试计数不再代表现状。

## 1. 结论

1. 仓库的用户侧产品入口已收敛为两个：WinUI 3 主体 `ClashSharp` 与唯一 WPF 安装器 `ClashSharp.Installer`。Rust、Cargo、Slint 源码和第二安装器入口扫描为零。
2. `ClashSharp.MihomoService` 与 `ClashSharp.RecoveryWatchdog` 是安装器载荷中的内部运行组件，不是独立用户产品；它们默认不可发布，只有显式 `ClashSharpFormalInstallerComponent=true` 的正式安装器组件构建可以发布。
3. 主体 MVVM 的依赖方向清晰，Core/Application/Infrastructure 与 ViewModel 不依赖 WinUI，View 和 code-behind 只承担平台交互、生命周期、对话框、导航、焦点与事件边界。现有架构测试已把这些约束变成可执行门禁。
4. 所有生产项目现在生成 XML 文档，并把公开契约缺失 `CS1591` 提升为错误。非显然的内部/私有权限、生命周期、重放、原生互操作和副作用边界必须写语义注释；不以复述代码的空注释换取“覆盖率”。
5. WinUI 3 触发器页面原有 13 个纯图标按钮已全部补齐本地化可访问名称与 tooltip，并有 XAML 契约测试防止回归。
6. 原生互操作已补齐 `System32` DLL 搜索路径限制，生产源码扫描缺失数为零；维护中的 34 个 PowerShell 函数均具有 comment-based help，PowerShell AST 解析错误为零。
7. Installer 的事务、清单、文件锁、受保护状态、认证 pipe、PID/签名校验、helper-only 证书 ownership、包验证、固定机器 payload/SCM 编排、生产 `IInstallerRuntime`、parent/coordinator 组合及 helper 启动入口均已有实现和测试。该生产执行路径仍受默认关闭的编译门保护；正式打包脚本不会启用它。真实签名候选的 Windows VM 故障矩阵尚未完成，因此当前不能宣称 Installer 可发布。

## 2. 当前产品与工程拓扑

审查快照包含 18 个 .NET 工程、925 个 C# 源文件、23 个 XAML 文件、7 个受版本控制的 PowerShell 脚本/模块；Rust/Cargo/Slint 实现文件为 0。

### 2.1 用户产品与内部组件

| 类别 | 工程/目录 | 责任与发布约束 |
|---|---|---|
| 主体产品 | `ClashSharp/ClashSharp` | WinUI 3 + MSIX 主程序；拥有用户交互和运行期控制，不拥有服务创建、配置或删除权限 |
| 唯一安装器 | `ClashSharp/ClashSharp.Installer` | WPF、`win-x64`、self-contained、single-file、`asInvoker`；唯一安装/修复/卸载用户入口 |
| 安装器内部载荷 | `ClashSharp.MihomoService`、`ClashSharp.RecoveryWatchdog` | 仅由正式安装器组件构建发布；不是额外产品或维护入口 |
| 安装器打包 | `ClashSharp/Installer` | 仅保留 PowerShell 打包契约、图标与签名输入目录；不再包含另一套 Installer 源码 |
| 集成探针 | `ProcessProbe`、`SettingsProbe`、`StartupProbe`、`TriggerProbe` | `IsTestProject=true`、`IsPublishable=false`、`IsPackable=false` |
| 隔离测试宿主 | `ClashSharp/SandboxTest` | PowerShell/Windows Sandbox 测试工具，不进入产品 payload |

工程门禁 `ExecutableTopology_HasOneMainApplicationAndOneInstallerProduct` 会拒绝第三个用户侧可执行产品，并验证内部服务/看门狗的条件式发布约束。

### 2.2 主体分层与 MVVM 方向

```text
WinUI View / Components
        │ 绑定、平台事件、生命周期
        ▼
ViewModel + presentation-facing contracts
        │ 构造注入
        ▼
Presentation adapters / composition ─────► Application use cases / ports
                                                │
                              Infrastructure ───┘（实现端口）
                                                │
                                                ▼
                                              Core

AppHost / composition root 负责组装具体实现和生命周期。
```

强制规则：

- Core、Application、Infrastructure 不引用 WinUI/Windows UI 命名空间；
- Model/ViewModel 不引用 UI 框架；
- ViewModel 不引用 `ClashSharp.Service`、不构造 `HttpClient`、不解析进程级服务单例；
- View code-behind 不使用服务定位器或具体 Service 命名空间；
- 数据型页面通过显式 `LoadAsync(CancellationToken)` 加载，Loaded/Unloaded 拥有取消生命周期；
- 构造函数只验证和保存依赖，不启动 I/O、后台任务或机器副作用；
- `async void` 仅允许在 WinUI/WPF 事件、Dispatcher 或框架覆盖边界，并在边界内完成异常归类。

### 2.3 Installer 分层

```text
ClashSharp.Installer (WPF composition / entry point)
        ├── ClashSharp.Installer.Presentation (shell state and commands)
        ├── ClashSharp.Installer.Core (protocol, journal, identity, authority state machine)
        └── ClashSharp.Installer.Windows (Win32, AppXSVC, certificate, SCM, ACL adapters)
```

`Installer.Core` 不依赖 WPF/Windows adapter；`Installer.Windows` 实现平台能力；WPF 层不得直接获得受保护 journal 写权限或机器 mutation capability。

## 3. 文档与编码规范

### 3.1 已落地的文档门禁

以下 10 个生产项目统一启用 `GenerateDocumentationFile=true` 与 `WarningsAsErrors += CS1591`：

- `ClashSharp.Core`
- `ClashSharp.Application`
- `ClashSharp.Infrastructure`
- `ClashSharp`
- `ClashSharp.MihomoService`
- `ClashSharp.RecoveryWatchdog`
- `ClashSharp.Installer.Core`
- `ClashSharp.Installer.Presentation`
- `ClashSharp.Installer.Windows`
- `ClashSharp.Installer`

公开类型、构造函数、方法、事件、字段、属性、枚举值、record 参数和 WinUI dependency-property 标识不得遗漏 XML 文档。`ProductionProjects_EnforceCompletePublicXmlDocumentation` 防止任一项目撤销门禁。

内部/私有符号采用信息价值门禁：凡涉及权限、所有权、线程、取消、资源寿命、原生调用、重放、状态切点、安全决定或不直观副作用，必须说明调用方需要保持的事实。简单的存储字段和自解释局部步骤不添加“获取/设置某值”式噪声。

PowerShell 规则同步写入 `CodingStyle.md`：每个维护函数都要有 `.SYNOPSIS`、`.DESCRIPTION` 和逐参数 `.PARAMETER`；当前 34/34 已满足，且新增仓库测试防止遗漏。

### 3.2 C# 14 与 LINQ 选择

项目固定 .NET 10 / C# 14，推荐：

- 固定快照使用 collection expressions；
- 不可变值契约使用 records；
- 闭合状态机使用 property/list patterns 与 switch expressions；
- 所有权使用 `using` / `await using` declarations；
- 边界参数使用 `ArgumentNullException.ThrowIfNull` 等显式守卫；
- 纯过滤、投影、分组、集合构造和精确基数检查积极使用 LINQ；
- 带 mutation cut-point、逐项取消、刻意串行 async、安全敏感中间态或需要精确诊断的流程使用显式循环；
- 不允许 deferred enumeration 穿越已释放资源或可变权限边界。

这不是“LINQ 越多越好”或“语法越短越新越好”；可审计的权限、顺序、资源寿命和失败诊断优先。

### 3.3 原生互操作

所有生产 `DllImport` / `LibraryImport` 均必须紧邻：

```csharp
[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
```

本轮修复了 40 个系统 DLL 入口并新增 `ProductionPInvokeDeclarations_AreRestrictedToSystem32`。当前扫描缺失数为 0。原生句柄继续优先使用 `SafeHandle`，平台错误在 adapter 内归类，不穿透到 ViewModel/UI。

## 4. WinUI 3 审查

### 4.1 MVVM 与生命周期

- ViewModel 以构造注入接收窄接口，平台操作由 Presentation adapter 实现；
- 页面数据在 Loaded 后异步加载，在 Unloaded/关闭时取消和释放；
- DispatcherQueue 只包裹 UI 工作，不包裹 I/O；
- 异常不会把原始路径、命令或系统异常文本直接显示给用户；
- 全局服务定位器债务已有架构测试基线并保持为 0。

### 4.2 可访问性

`View/Triggers.xaml` 的 13 个 glyph-only `Button` 已全部具有：

- 本地化 `AutomationProperties.Name`；
- 同值或等价的本地化 `ToolTipService.ToolTip`；
- 原生 Button 的键盘、焦点与 UIA 行为；
- 对应英文、简体中文、繁体中文、俄文、法文、德文资源。

无内容的启用开关也具有可访问名称。`TriggersXaml_GlyphOnlyButtonsExposeAccessibleNamesAndTooltips` 会精确检查这些控件。

### 4.3 Binding 选择

当前主体 XAML 大量使用 `{Binding}`，主要来自动态 DataContext、DataTemplate 和运行时 ViewModel 组合。没有证据支持全量改成 `x:Bind`；这种迁移可能改变生命周期、模板上下文和 converter 语义。

后续只在页面自身属性、稳定类型和可编译验证的场景逐项引入 `x:Bind`，每次迁移都要求 UI 状态、卸载取消、模板与可访问性测试保持不变。

## 5. Installer 专项审查

### 5.1 已实现的安全基础

- 唯一 C# / WPF Installer，Rust/Cargo/Slint 回归由拓扑测试拒绝；
- 严格、内嵌、签名候选绑定的 release manifest；
- sibling payload 普通文件/目录锁定、MSIX identity 与机器文件逐项 length/SHA-256 校验；
- operation-specific durable journal、canonical codec/hash、CAS store、helper-authoritative phase session/loop、回执丢失重放和 helper clear receipt；
- 受保护 `%ProgramData%` 根、只读 parent reader、rename/reparse/ACL 防护；
- `runas` 单 UAC 生命周期、named pipe DACL、first-instance、双方 PID、父进程镜像/签名校验、bounded host loop 与 helper authority resource lifetime；
- WPF `Program` 的 helper 路由会从当前签名可执行文件加载内嵌 manifest，并进入 `WindowsInstallerMachineHelper`；它不创建 WPF Application；
- exact target SID 的包注册检查、TrustedPeople 证书 adapter、helper-only durable certificate ownership mutation/verification，以及供 parent coordinator 使用的只读证书后置条件 adapter；
- target profile、machine deployment plan、payload archive/staging/promotion/removal、service configuration/mutation、association store、root cleanup，以及固定 `WindowsMachineHelperMachineOperations` 编排；
- 生产 `ProductionInstallerRuntime` 与 `WindowsInstallerParentEngine`：由可信 backend 独占当前 SID、发布身份和 request 构造，执行前只读检查平台、包、进程及受保护事务，并对单次操作实行 single-flight；
- `InstallerRuntimeReadiness.AllowedOperations` 由可信 runtime 显式给出，ViewModel 不再根据产品状态推断权限；不受支持平台上的已安装产品只暴露 Uninstall，非法操作集合会整体 fail closed；
- parent 的执行会按操作创建 bounded broker/coordinator/helper session；证书 mutation/ownership writer 仍只存在于 helper，parent 只持有 `WindowsInstallerCertificatePostcondition` 与受保护事务 reader；
- helper machine backend 已覆盖普通 install/repair/uninstall 与 durable replay；目标用户 profile 已删除时，只能在精确 SID association 仍存在且固定服务已独立证明 absent 后，用只读根 lease 清理固定 payload/association。显式跨 owner reassociation 尚未形成可验证的双 owner root/SCM 协议，当前继续 fail closed；
- WPF `App` 仅在 `CLASHSHARP_INSTALLER_MUTATION_RUNTIME` 编译符号存在时组合生产 runtime，否则构造不可执行的迁移预览 runtime；
- `InstallerShellViewModel` 拥有操作 generation、取消和 runtime 释放；WPF 窗口关闭只请求协作取消，并等待 `IsBusy=false` 后再完成关闭；
- 主体无服务 create/config/delete 权限，安装器独占服务生命周期 mutation。

### 5.2 当前发布阻断项

生产 UI runtime、parent engine、coordinator/broker/helper 组合已经进入源码，但尚未进入任何默认或正式产物：`ClashSharpEnableInstallerMutationRuntime` 默认是 `false`；即使显式设为 `true`，项目也要求同时存在 `ClashSharpFormalInstallerBuild=true` 与有效内嵌 release manifest。当前 `Installer/build.ps1` 只传入 formal-build/manifest 参数，没有传入 mutation-runtime 参数，因此其产物仍使用 `MigrationPreviewInstallerRuntime`，`CanExecute=false`。这是等待 E4 证据期间的预期 fail-closed 状态，不是遗漏接线，也不能作为可发布证明。

在移除该阻断之前必须同时完成：

1. 在标准用户与 over-the-shoulder 管理员凭据两种 Windows 11 x64 VM 中闭环 install/repair/uninstall，验证 ACL、SCM、AppXSVC、目标 SID 证书、包与进程观测、UAC 取消，并证明每个正常事务只出现一次 UAC；
2. 对每个 durable cut-point 执行崩溃、断连、回执丢失、helper/parent 终止、重启、篡改与重放矩阵，并证明恢复结果和受保护状态收敛；
3. 为换绑提供独立、明确确认的 Repair UI/parent policy；只有该确认路径才能构造 `AllowReassociation=true`，并在 VM 中验证外来 owner 证据不会被普通 Repair 覆盖；
4. 用同一最终 Authenticode 签名候选验证内嵌 manifest、双方映像信任、helper 自举、RFC3161 时间戳、安装/修复/升级/卸载/换绑与恢复矩阵；
5. 将 Sandbox 当前明确 skipped 的 launch、startup、cleanup-uninstall 场景实现为“未执行即失败”的证据，禁止把 skipped 当通过；
6. 只有前述证据全部归档后，正式打包才可显式传入 `ClashSharpEnableInstallerMutationRuntime=true`；默认源码构建与开发产物继续保持关闭，并由测试锁定“只能在 formal build + embedded manifest 下启用”。

### 5.3 发布判定

| 证据级别 | 含义 | 当前状态 |
|---|---|---|
| E0 | 文档/设计 | 已有 |
| E1 | 静态契约/源码审查 | 已有 |
| E2 | Release 构建与确定性测试 | 当前 Installer 安全集 983 项通过（Core 547 + Presentation 88 + Windows 348）；WPF Release/x64 0 warning、0 error |
| E3 | Windows 真实 API/集成环境 | 部分 adapter 测试已有，完整安装路径未闭环 |
| E4 | 签名候选 VM、故障注入、重启恢复 | 未完成，发布硬阻断 |

## 6. 本轮已实施的优化

- 修复主体、组件、ViewModel、服务和 Installer 的公开 XML 文档缺口，并把文档生成/`CS1591` 锁入全部生产项目；
- 扩充 `CodingStyle.md` 的 MVVM、C# 14、LINQ、WinUI 3、原生互操作与 PowerShell 规则；
- 为 Triggers 的 13 个纯图标按钮补齐六语言可访问名称和 tooltip；
- 为 40 个生产 P/Invoke 添加 `System32` 搜索路径限制；
- 为 22 个原缺失帮助的脚本函数补齐 comment-based help，并扩充 12 个打包模块函数的参数/行为说明；
- 将 MihomoService/RecoveryWatchdog 默认设为不可发布，只允许正式安装器组件构建发布；
- 新增文档、P/Invoke、PowerShell 帮助、可访问性和双产品拓扑架构测试；
- 完成 Installer 生产 presentation runtime、显式 allowed-operation 授权、窗口关闭取消/资源释放、Windows parent engine、认证 helper composition、固定机器 mutation，以及 profile 缺失时受 service-absent 证明约束的固定根卸载收敛；跨 owner reassociation 仍保持拒绝，并以默认关闭的 formal-build gate 保持 fail-closed。

## 7. 验证账本

下表记录最终稳定源码快照。开发机可安全执行的范围内，任何失败、意外 skipped/not-executed、格式差异或警告都会阻止推送；明确隔离的真实证书存储测试继续作为 E3/E4 发布门禁，不在开发机执行。

| 检查 | 当前证据 |
|---|---|
| NuGet vulnerability（含 transitive，18 项目） | 0 个已知易受攻击包 |
| PowerShell AST / 函数帮助 | 7 文件解析 0 错误；34/34 函数有帮助 |
| 生产 P/Invoke System32 限制 | 0 个缺失 |
| 公开 XML 文档门禁 | 10/10 生产项目启用；Release build 验证 |
| Installer Core tests | 547/547 通过；0 失败；0 skipped |
| Installer Presentation tests | 88/88 通过；0 失败；0 skipped |
| Installer Windows 安全集 | 348/348 通过；0 失败；0 skipped；明确排除会修改开发机 `CurrentUser\TrustedPeople` 的 `WindowsCurrentUserCertificateStoreAdapterTests` |
| 主体 `ClashSharp.Tests` | 2239/2239 通过；0 失败；0 skipped |
| 主体 Architecture tests | 172/172 通过；0 失败；0 skipped |
| 主体 MVVM structure gate | 16/16 通过；0 失败；0 skipped |
| Release x64 solution build | 18 项目成功；0 warning；0 error |
| `dotnet format --verify-no-changes` | 通过，退出码 0 |
| `git diff --check` | 通过；提交前再次复核 |

## 8. 后续开发优先级

### P0：Installer 可发布闭环

完成第 5.2 节全部 E3/E4、显式双 owner reassociation 协议与 Sandbox 证据。在这之前不得让 `build.ps1` 传入 `ClashSharpEnableInstallerMutationRuntime=true`，不得改变默认关闭值，也不得放宽 protected root ACL。

### P1：可执行验证与文档质量持续门禁

- 把 PowerShell AST 语法检查纳入 CI，而不只依赖 C# 源码契约；
- 为所有新 internal/private 权限与生命周期边界执行审查，拒绝占位式 XML/comment help；
- 将 UIA、键盘、高对比度、DPI、窗口关闭取消与无障碍屏幕阅读器检查纳入 Windows UI 测试；
- 完成 Sandbox 的非 skipped 安装、启动、代理关闭和清理卸载矩阵。

### P2：受证据驱动的现代化

- 逐页评估可稳定使用 `x:Bind` 的页面自有属性，不迁移动态模板绑定；
- 在不改变副作用顺序的纯数据转换中继续用 LINQ 收敛重复代码；
- 对大型 View/code-behind 继续按生命周期、dialog、drag/drop、focus 等平台职责拆分，不把平台类型下沉到 ViewModel。

## 9. 提交与推送退出条件

1. 外部并发修改稳定并纳入最终 diff 审查；
2. `dotnet format --verify-no-changes`、`git diff --check` 全绿；
3. Release x64 solution 0 warning / 0 error；
4. 主体、Installer Core、Presentation 与 Windows 开发机安全集全部执行、0 失败、0 skipped/not-executed；真实 `CurrentUser` 证书存储测试继续留在隔离 VM 发布门禁；
5. 本文件验证账本更新为最终真实结果；
6. 只在上述条件满足后提交，并直接 `git push` 到 `main`。
