# ClashSharp 主线整理与开发状态（2026-09-22）

当前正在目标 Windows Server 进行真实界面、功能和安装器验收，首轮问题与未测项见[实机验收记录](2026-09-22-server-ui-acceptance.md)。

9 月 23 日继续验收：流量累计与停止收尾、触发器输入持久化、列表开关、参数下拉框和选项弹窗已有真实服务器证据。`84eb75b` 进一步通过空分类刷新、分类与级别筛选、组合搜索，以及启动触发后的通知和两条执行日志核对；完整主程序 3,122 项测试与格式检查通过。`a7d16d7` 修复删除最后一个条件或行为后误显示全部参数，18 项目构建、147 项相关回归及格式检查通过，已在服务器全新安装并核对 DLL，空参数区域实测正确，CI 通过。运行时长 5.5 秒的任务实际关闭采样且没有周期性重复执行；无效时长的页顶提示误写为流量阈值，修复已通过 148 项回归，等待新包复验。运行时长与定时 AND 条件已实际按时执行一次。新增时间摘要精度和后台改变采样后设置开关显示过期的问题，源码修复经 3,124 项完整回归及最终 314 项相关检查，等待新候选实测。七动作顺序执行和终末退出已在 a7d16d7 通过；d7b3af6 已全新安装、DLL 核对一致、远端 CI 通过。安装期间发现长时间无响应，后台执行修复通过 148 项安装器界面层测试；排序按钮边界和透明代理文案修复通过 470 项相关检查，等待新候选实测。当前矩阵为 60 项通过、27 项部分完成、54 项待测；采样统计、清空历史、其余交互及资源清理尚未完成，不能作为完整发布验收。搜索清除图标首次点击只聚焦的现象另行待复现。本地与远端仍仅有 `main`，开放 PR 为零。

> 本文保留归并提交 `68f450a` 的历史状态和验证数字。后续普通卸载的自动空目录清理已接通，见[清理接入记录](2026-09-22-installer-cleanup-integration.md)；设置页普通偏好与分组重置已接入可等待的生产入口，见[异步偏好接入记录](2026-09-22-settings-preference-commands.md)。完整设置权威切换和发布验收继续推进。

本次恢复开发并统一到 `main`。版本仍为 `1.0.0`，程序集和 MSIX 仍为 `1.0.0.0`；项目尚未完成正式发布。源码归并、生产接入、本机测试、隔离环境验收分别记录，不能互相替代。

## 仓库与 PR

开始时远端 `main` 为 `a1b1cd8`，本地工作目录仍在 `feat/settings-generation@0508a35`，并有一处将安装器窗口改名为“ClashSharp 管理程序”的未提交修改。另有 `fix/installer-empty-cleanup@3b95e93`、`fix/server-readiness-main@065a5d5` 两个工作树。所有现有修改和独有提交均保留。

| 原入口 | 处理 |
| --- | --- |
| PR [#1](https://github.com/Water-Run/ClashSharp/pull/1)、[#2](https://github.com/Water-Run/ClashSharp/pull/2)、[#3](https://github.com/Water-Run/ClashSharp/pull/3) | 均已合并，无遗留开放评审 |
| PR [#5](https://github.com/Water-Run/ClashSharp/pull/5) / `feat/settings-generation` | 已合并为 `af35185`，归并设置基础设施、生产凭据与外观修复、绿色管理程序品牌 |
| `wip/installer-empty-cleanup` / `fix/installer-empty-cleanup` | 通过 merge 保留 `3b95e93` 的完整提交与清理模块，修复联合构建及格式问题；移除未完成的生产创建钩子 |
| `fix/server-readiness-main` | 提交已属于主线，无独有功能；额外工作树改为 detached HEAD，保留其文件 |
| 分支策略 | 本地及 origin 仅保留 `main`；历史检查点标签继续保留。后续从 `main` 和本文件继续开发 |

## 当前实现

| 领域 | 已实现 | 尚未完成 |
| --- | --- | --- |
| 主体 / MVVM | Core、Application、Infrastructure、Presentation 分层，WinUI 页面、磁贴、配置与订阅、触发器、连接筛选、日志和统计 | 新候选的完整页面交互、正常退出与长期运行验收 |
| 代理运行 | 主控四种模式、系统代理与 TUN、服务 IPC、Watchdog；默认 DIRECT 配置与配置提交重试修复 | 同一候选的完整代理、TUN、故障和重启矩阵 |
| 设置 | 原有页面事务仍使用唯一 LocalSettings 权威；JSON migration/session/facade、六类参与者及 Profile/Log 退休能力已纳入主线 | 生产代际容器、全部消费者、导入/重置与退出的整体切换 |
| 控制端凭据 / 外观 | 独立凭据服务已接入启动、控制端请求、配置生成与全数据清理；强调色根据实际 WinUI 资源验证 | 完整外观代际装配和真实窗口交互 |
| 安装管理程序 | WPF 单页维护、安装/修复/卸载、Server Desktop 支持、目录结果协议；绿色品牌与“管理程序”标题 | 目录归属、终结恢复与原生删除的完整接入，以及新包的原生验收 |
| 语言 | 简中、繁中、英、俄、法、德、波斯语；波斯语 RTL | 波斯语实际页面点选、键盘和布局验收 |
| 构建 / 发行 | SDK 与依赖锁定、单一 CI、离线开发安装包、固定输入与载荷验证 | 正式签名候选、完整隔离验收、发布提升流程与发行文档 |

## 本轮修复

- 修复 `LocalizationService.GetString` 与主窗口 RTL 绑定中的 CA1304：显式传入 `CultureInfo.CurrentUICulture`，保持自动检测使用系统 UI 文化。此前远端 [CI 35178730997](https://github.com/Water-Run/ClashSharp/actions/runs/35178730997) 的构建和打包均被这两处错误阻断。
- 修复波斯语加入后遗漏的三处测试断言：语言列表八项包含自动检测，设置 registry 接受 `Persian`，页面能选择并保存波斯语且不提前重建语言选项。
- 修复清理分支的 CA1861 和两处格式问题，完成首次与最新主线、设置分支的统一编译和测试。
- 清理分支原来只在 `WindowsInstallerDirectoryNative.CreateDirectory` 接入了归属记录，但生产 helper、parent reader 和终结器尚未接通。主线恢复原有目录创建路径，保留独立模块和测试，避免安装流程写入尚不能消费的机器级账本。自动空目录清理仍明确未启用。
- 保留四语 README 与波斯语更新，统一其当前状态入口；历史报告加上快照说明，恢复位置改为可长期访问的提交链接。

## 验证

使用 Windows x64 与仓库固定 SDK `10.0.201`，完成以下本机验证：

| 检查 | 结果 |
| --- | --- |
| locked restore / 18 项目 Release x64 build | 通过，零警告、零错误，未改变依赖锁文件 |
| 主程序 | 2938 / 2938 通过 |
| Installer Core | 1022 / 1022 通过 |
| Installer Presentation | 143 / 143 通过 |
| Installer Windows 本机安全集 | 1075 / 1075 通过 |
| 本机测试合计 | **5178 / 5178**，零失败、零跳过 |
| 完整 format | 1539 文件，零变更，无工作区加载警告 |
| PowerShell 5.1 / 7 | 各解析 21 个源码脚本，零语法错误 |
| 安装器图标 / runtime / profile / 第三方声明 | 全部通过；runtime 1 正例和 19 反例，第三方契约 30 场景 / 53 断言 |
| Sandbox 报告 / 启动证据 | 两个 PowerShell 版本各通过 111 / 16 项断言 |

PR 候选 `83f3058` 的[两项远端 CI](https://github.com/Water-Run/ClashSharp/actions/runs/35724023117) 均通过：.NET 构建、格式、测试与覆盖率门禁，以及完整离线开发安装包。已下载四份 TRX 并独立核对：主程序 2938、Core 1022、Presentation 143、Windows 1095，共 **5198 / 5198**，零失败、零跳过。开发包归档为 317846956 字节，artifact ID `10692941057`，仍是未签名验证产物。

PR 合并提交 `af35185` 的 Git tree 与 `83f3058` 完全一致；后续收据更新仅修改本文。远端归并与分支删除后，全部四个历史分支头均为 `main` 的祖先，开放 PR 为零，本地和远端分支仅 `main`。可复现命令：

```powershell
dotnet restore ClashSharp/ClashSharp.slnx --locked-mode
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet test ClashSharp/ClashSharp.Installer.Tests/ClashSharp.Installer.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Presentation.Tests/ClashSharp.Installer.Presentation.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Windows.Tests/ClashSharp.Installer.Windows.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName!~WindowsCurrentUserCertificateStoreAdapterTests&FullyQualifiedName!~WindowsTargetUserCertificateStoreAdapterTests'
```

格式检查按 [CodingStyle.md](../../CodingStyle.md) 设置 `Platform=x64`。同时执行 CI 中的 PowerShell 5.1/7 语法、图标、打包 runtime、构建 profile、第三方声明与 Sandbox 报告/启动证据契约。本机按类排除两个包含实际证书存储操作的测试类，共 20 项；隔离 Windows CI 全量执行并通过这 20 项，所以本机和 CI 总数不同。日志和 TRX 位于被 Git 忽略的 `artifacts/verification/consolidation-*`。

本轮没有执行正式签名发布、安装卸载或真实代理接管。2026-09-12 的实机记录继续绑定当时的具体候选，不作为新归并代码的原生验收证明。

## 后续实现顺序

1. **自动空目录清理**：接通 protected stores 的 terminal store、parent reader 的终结恢复、helper 的 Clear 回复前回调与资源释放；再同时启用目录创建归属。补齐中断、重放、权限、对象替换及正常安装/修复/卸载验收。旧账户独立卸载需要单独纳入协议。见[清理设计](../design/2026-09-12-installer-empty-directory-cleanup.md)。
2. **设置代际生产切换**：组装 generation scope 和各参与者；切换页面、磁贴、触发器、Profile/Log 消费者；统一导入/重置与退出排空；验证 desired/applied、补偿、持久化失败和重启恢复后再替换现有单一权威门禁。见[设置设计](../design/2026-09-08-settings-generation-cutover.md)。
3. **产品验收**：对同一新候选补齐 WPF/WinUI 操作、正常退出、RTL、标准用户/alternate-admin、故障恢复与重启矩阵。
4. **正式发行**：受控签名、完整许可归档、SBOM/provenance、候选到发布的同一 digest 验证及支持/安全/更新说明全部闭环后，才发布正式 1.0.0。

源码与遗留工作已统一保存；上述生产接入和发行事项仍是实际剩余工作，不能因 PR 合并或分支删除而标记完成。
