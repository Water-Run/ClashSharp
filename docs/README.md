# ClashSharp 开发文档

用户已在 `cdfd835`／`dc3bf078` 内部候选上完成新一轮安装与导入手验。本轮正在修复反馈，并以明确的四档通知矩阵覆盖全部操作边界；后续验证保留新产生的配置与设置。最新实现、验证及交付状态见手验反馈记录。此前候选的完整 CI7554 项和无预装产品证书的实际安装／卸载属于上一轮已完成证据。

| 入口 | 用途 |
| --- | --- |
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
