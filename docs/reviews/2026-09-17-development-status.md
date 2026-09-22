# ClashSharp 开发状态（2026-09-17）

> 历史快照。2026-09-22 已恢复开发并归并工作到 `main`；本页的暂停、PR 和分支说明仅描述当时状态。当前入口见[主线整理与开发状态](2026-09-22-development-status.md)。

日期：2026-09-17（Asia/Shanghai）  
核对基线：远端 `origin/main` 在开始时为 `b2f1842`，与本地 `main` 一致。  
定位：把 1.0.0 暂停之后的仓库状态、未合并工作入口、本轮文档与语言工作落成一份当前快照。历史里程碑与测试数量仍以 [1.0.0 执行账本](1.0.0-execution-ledger.md) 和 [暂停检查点](2026-09-12-pause-checkpoint.md) 为准。

## 1. 当前判断

1.0.0 **没有完成正式发行**。产品语义版本仍是 `1.0.0`，程序集与 MSIX 版本仍是 `1.0.0.0`。开发按用户要求停在 2026-09-12 的已验证暂停检查点，之后 `main` 只追加了 README 整理和本轮语言/文档工作，没有重开安装器或设置代际的生产切换。

| 项 | 当前位置 |
|---|---|
| 已验证暂停检查点 | `main` [`065a5d5`](https://github.com/Water-Run/ClashSharp/commit/065a5d5)，标签 [`v1.0.0-checkpoint.20260912`](https://github.com/Water-Run/ClashSharp/releases/tag/v1.0.0-checkpoint.20260912) |
| 检查点后的 `main` 文档 | [`b2f1842`](https://github.com/Water-Run/ClashSharp/commit/b2f1842) 重构英文/中文 README |
| 生产偏好权威 | 仍是唯一 LocalSettings；设置代际切换未进入 `main` |
| 打开的 Pull Request | **无**。草稿 PR [#5](https://github.com/Water-Run/ClashSharp/pull/5) 已于 2026-09-17 关闭，不合并 |
| 保留的工作分支 | `feat/settings-generation`（设置代际）、`wip/installer-empty-cleanup`（空目录清理） |

检查点处已核过的能力：4800 项本机测试、18 项目 Release x64 构建、真实安装/修复/卸载、核心重载与崩溃恢复、现有 Clash 配置 32/32 节点探测。未完成的能力没有因为关闭 PR 而消失，只是不再以 Open PR 的形式出现。

## 2. 仓库入口

仓库不再保留 Open 的 Pull Request。未完成实现继续以分支保存：

| 入口 | 提交 | 内容 | 与 `main` 的关系 |
|---|---|---|---|
| [`feat/settings-generation`](https://github.com/Water-Run/ClashSharp/tree/0508a35df7b0755c86461afaf255814e9e49b4d3) | 关闭前的 PR #5 头 | 设置代际权威、参与者、仓库寿命、凭证与 Appearance/Network/Triggers 的 owned 应用 | 安装器/启动修复已独立回移植到 `main` `db21085` 及后续检查点提交。生产消费者切换、导入/重置组合、退出验收未完成，**不得当作可合并发行** |
| [`wip/installer-empty-cleanup`](https://github.com/Water-Run/ClashSharp/tree/3b95e93456918d58eb8a437a2d8905f642b2a3d6) | `3b95e93` | 未完成的目录归属、terminal store、原生删除与新测试 | `main` 已有目录清理报告与成功 Clear 前扩展点，但生产原生删除未接通 |

恢复顺序不变：先从对应分支完成未接通部分并验证，再推进新的 `main` 节点。不要把已关闭的草稿 PR 当成丢弃的源码。

## 3. 产品完成度

| 领域 | 已在 `main` 落地 | 恢复开发时仍需完成 |
|---|---|---|
| 主体 / MVVM | Core、Application、Infrastructure、Presentation 分层与架构测试 | 按权限、取消和资源所有权审查新增代码 |
| 代理运行 | 主控四种模式、系统代理与 TUN、服务 IPC、Watchdog | 同一签名包的完整页面点击、正常退出、多会话不在范围内 |
| 安装器 | WPF 单页、机器/用户证书、Server 2025 Desktop、真实安装修复卸载 | 自动空目录清理、完整发布矩阵、正式签名发行 |
| 设置 | 页面事务、采样/启动/导入/重置协调器 | 生产设置代际权威切换（见 `feat/settings-generation`） |
| 界面语言 | 简体中文、繁體中文、English、Русский、Français、Deutsch、فارسی；波斯语使用 RTL 布局 | 本机 WinUI 页面在波斯语下的点击验收仍待恢复开发后补 |
| 文档 | 中英俄波 README；执行账本、暂停检查点、实机验收 | 正式发行说明、SECURITY 与变更记录仍随发布矩阵 |

## 4. 本轮落成（2026-09-17）

- 同步远端 `main`。仓库在开始时已与 `origin/main` 一致。
- 关闭草稿 PR #5，并在 PR 中写明：不合并、分支保留、安装器修复已在 `main`。关闭后 `gh pr list --state open` 为空。
- 界面增加波斯语（`AppLanguage.Persian` / `fa` / `fa-IR`）完整目录，与英文 730 个键对齐；自动检测 `fa`；主窗口按语言绑定 `FlowDirection`。俄语目录此前已在产品中。
- README 增加 Русский / فارسی 版本，四语切换，并把当前开发状态链入文档表。

本轮没有重跑 4800 项 Windows 测试：本机是 Linux，仓库 SDK 钉死 `10.0.201`，WinUI 测试目标是 `net10.0-windows10.0.22000.0`。语言目录用脚本核对了键集合、格式占位符和架构测试所需子串。完整构建与测试仍以 Windows CI 为准。

## 5. 下一步（仅在恢复 1.0.0 之后）

1. 从 `wip/installer-empty-cleanup` 接通生产原生空目录删除，并做隔离验收。
2. 从 `feat/settings-generation` 做生产设置/消费者一次切换，不要把草稿分支直接合并进暂停后的 `main`。
3. 补齐 WPF/WinUI 页面交互、正常退出、波斯语 RTL 实机点选，以及完整发布矩阵。
4. 只有上述项带着新的验证证据回到 `main` 之后，才考虑正式发行。
