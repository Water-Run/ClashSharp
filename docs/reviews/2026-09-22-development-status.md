# ClashSharp 主线整理与开发状态（2026-09-22）

当前正在目标 Windows Server 进行真实界面、功能和安装器验收，首轮问题与未测项见[实机验收记录](2026-09-22-server-ui-acceptance.md)。

9 月 23 日 18:30 UTC：当前服务器安装 `7a9561d`，CI 35899439277 通过，安装 DLL 与候选载荷散列一致。227cb08、3d5bad4、3e30bec、0492ead、fb43a6d、9a0db48、41344d8 正常卸载后均核实包/服务及六处自有目录消失，随后正常安装成功；验收数据另从关闭后的散列备份恢复，不计自动升级保留数据通过。此前 ef6bfa7 已通过“修复中断且应用包缺失”的真实恢复，未手工注册载荷。俄语启动引导及首页标题均已通过最小窗口复验；德语、俄语、法语、繁体中文磁贴遍历和编辑器补入实机证据，繁体中文关键词筛选正常。关闭确认的 Esc、取消、确认退出以及直接退出已实测，磁贴同步显示偏好。新发现内核内存详情重复说明，源码已统一去重，构建及 121 项回归通过，等待候选包复验。

此前 `5ae3ac6` 已验证内置配置重命名/删除按钮禁用、英文节点地区文字完整显示。空闲采样修复经过首次启动和设置页重启两次实测，均无 ConnectionSampling 警告、连接或虚假流量记录。

`29fe10d` 已验证触发器新建/已有草稿跨页保留、显式保存与取消、条件和行为搜索及键盘提交；英文主页面切换、托盘双击恢复和页面菜单、最小窗口内部分页面与对话框可用。浅色、深色、跟随系统选项切换正常，系统实时主题变化仍待测。旧版正常卸载前已备份状态，之后包、服务和六处产品目录均为空。

两个安装器并发修复的笼统失败诊断仍待实机复验。`bf09ff2` 已区分通信通道创建失败与提权失败，并添加等待其他操作后重新检查的提示；18 项目构建、50 项相关 Windows 回归、152 项完整界面层测试及格式检查通过。服务器 12:09 UTC 打包完成、CI 通过，该候选尚未替换当前安装版。

用户追加首页磁贴专项：推荐布局从 8 张扩展到 20 张，可选磁贴从 58 张增加到 68 张。已实测编辑器最小/恢复/最大窗口、取消与搜索、拖动后进程重启保留、键盘跨行遍历及详情、停止内核后跨页面保留会话流量。新增订阅余额/超额、单向已知流量及过期详情已通过完整/部分/缺失/超额/过期实测。批量选择和计数、波斯语编辑器、隐藏全部后重启、恢复推荐保存、全部 68 项保存和重启保留均有实机证据。fb43a6d 已复验引导深色样式、异步磁贴焦点保持、诊断弹窗主题/Tab/Esc、四项统计文案与底层计数；主程序完整回归 3172 / 3172。9a0db48 修复并实测端口占用后的原因展示、重复重试及释放后恢复，463 项相关回归通过。完整范围与限制见[磁贴扩充与验收](2026-09-23-dashboard-tiles.md)。

当前矩阵 **69 项通过、33 项部分完成、39 项待测**；完整多语言与 RTL、小窗口/键盘/托盘、采样统计与清理、安装器剩余故障场景和临时资源回收尚未完成，不能作为完整发布验收。18:03 UTC 再核对本地与远端仅有 `main`，开放 PR 为零。生产设置权威切换仍是独立未完成事项。
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
