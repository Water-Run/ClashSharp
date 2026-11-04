# ClashSharp 开发文档

当前交付为产品源码 `fdb101e`／内部候选 `eb436a56`：完整 CI **7643／0／0**，签名生产与独立载荷检查通过。蓝机已按“保护数据→匹配卸载→安装→精确恢复”更新并启动，桌面 `Clash#` 为完整 11 成员解压目录。本轮完成 WinUI 3 Fluent 样式与交互收敛、RDP 剪贴板根因修复和通知级别收敛，详见[本轮记录](reviews/2026-11-04-fluent-production-pass.md)。

| 入口 | 用途 |
| --- | --- |
| [Fluent 样式、交互与通知收敛](reviews/2026-11-04-fluent-production-pass.md) | 当前包、剪贴板根因、通知级别新策略、安装器调整与实机交付 |
| [磁贴、关于页与当前冻结](reviews/2026-11-03-tile-reorder-and-about.md) | 当前包、数据保护、用户暂停及未验收项目 |
| [手验反馈与通知覆盖清单](reviews/2026-11-02-manual-feedback-and-notification-matrix.md) | 当前修复范围、四档通知事件矩阵、实现和候选验证进度 |
| [干净环境 Setup 启动与指引](reviews/2026-11-02-installer-clean-start-and-guidance.md) | 当前修正版、无证书准备的实际安装及干净解压目录交接 |
| [蓝机卸载与空间清理](reviews/2026-11-02-blue-uninstall-and-cleanup.md) | 当前未安装状态、唯一原始安装包及 C 盘空间 |
| [蓝机部署与实机回归](reviews/2026-11-02-blue-deployment-acceptance.md) | 清理前的 17 项实机检查、真实修复与历史数据保护 |
| [安装选项风格统一](reviews/2026-11-01-installer-options-style.md) | 最新安装器布局、通知名称、推荐设置恢复与实机交付 |
| [基础导入与通知系统修正](reviews/2026-11-01-basic-workflows-and-notifications.md) | 本轮基础兼容、导入体验、通知级别覆盖与 Mihomo 升级进展 |
| [当前实现与开发计划](reviews/2026-11-01-development-status-and-plan.md) | 当前源码、已验候选、模块实现、验证结果、优先级和完成标准 |
| [1.0.0 执行账本](reviews/1.0.0-execution-ledger.md) | 持续追加实施结果、候选身份和验收证据 |
| [用户手验清单](reviews/2026-10-26-manual-handoff-checklist.md) | 冻结候选、用户检查项、数据保护与维护入口 |
| [验收范围与交付顺序](reviews/2026-10-26-validation-scope.md) | 既定 Server 2025 验收范围及用户反馈、修复、发布顺序 |
| [安装器构建说明](../ClashSharp/Installer/README.md) | 安装载荷、开发构建、正式入口和打包约束 |
| [Agent 接入指南](guides/native-agent.zh-CN.md) | MCP 客户端配置、权限和排障；另有 [English](guides/native-agent.md) |
| [编码约定](../CodingStyle.md) | 分层、生命周期、错误处理、文档和验证要求 |

## 历史资料

- `design/` 保存具体功能的设计决策。
- `reviews/` 保存按日期记录的审查与验收快照。旧文档中的待验项，应先与最新执行账本核对。
- [架构稳定化账本](architecture/stabilization-ledger.md)保存逐项关闭证据；其中的 Open 或 In Progress 不直接代表当前产品缺少该功能。
- `superpowers/` 保存早期架构方案与实施计划；继续开发以当前源码、现行计划及验收约定为准。

记录进展时区分源码实现、自动验证、指定候选实机验证和用户验收。仅执行文档范围检查的 CI 成功不能替代产品构建与测试，内部签名候选也不能代替正式发布。
