# 1.0.0 暂停检查点（2026-09-12）

用户要求收尾、推送一个版本并暂停。本次保存 **1.0.0 阶段版本**，程序集及 MSIX 版本保持 `1.0.0.0`；未将其标记为完成全部正式发行验收。

## 本次 main 内容

既有 `db21085` 已包含默认 DIRECT 自引用修复、配置提交的有限重试、Server 2025 Desktop 平台支持与安装器取消反馈。本次加入 `96e9cf7` 和 `2c0266b`：事务 reader 每次读取结束释放目录保护，关闭等待已接收读取；新增受原事务身份约束的目录清理报告、成功 Clear 回复前扩展点，以及 WPF 对已清理/明确保留目录的展示。

生产原生目录删除尚未接入此扩展点，普通结果没有报告时保持原有文案。本次 main 不包含未完成的目录归属账本与 native 清理模块。

## 验证

- 本次生产源码 `2c0266b` 的独立 main 工作树锁定还原、18 项目 Release x64 构建通过，零警告、零错误。完整本机测试 **4800/4800**（主程序 2643、Core 1022、Presentation 143、Windows 安全集 992），零失败、零跳过；六项真实证书测试保留给隔离 CI。格式检查发现并修正一处 `with` 初始化器换行，没有语义改动；最终格式检查覆盖 1449 个文件，零处变更。完整 TRX、构建与格式日志保存于本地暂停收据目录。
- 同一 WPF/Presentation 源码的实际编译页面通过 25 项离屏检查与 9 张渲染，已目视确认最小窗口中保留目录及原因可通过滚动查看、关闭按钮可见。无正常窗口展示或原生系统修改，不能替代完整点击验收。
- 先前准确 `4640685` 完整服务器包已通过原生安装、WinUI 首次启动、修复和卸载；准确包摘要、测试签名与清理边界见[实机验收记录](2026-09-12-server-acceptance.md)。本次 reader/结果协议改动没有复用该包作为新版本实机证明。

## 保留工作及恢复入口

- [wip/installer-empty-cleanup](https://github.com/Water-Run/ClashSharp/tree/wip/installer-empty-cleanup) 的 `3b95e93` 保存未完成的目录归属、terminal store、原生删除与新测试。最终完整编译、生产装配及服务器自动清理未验收；恢复步骤见该分支的 `docs/design/2026-09-12-installer-empty-directory-cleanup.md`。
- [feat/settings-generation](https://github.com/Water-Run/ClashSharp/tree/feat/settings-generation) 和[草稿 PR #5](https://github.com/Water-Run/ClashSharp/pull/5) 保存设置代际开发。生产仍以 LocalSettings 为唯一偏好权威，完整页面消费者切换、导入/重置组合及退出验收待续。
- 本机与服务器代理指纹保持。服务器本轮测试包、服务、进程和临时签名材料已清理；本次暂停没有启动新的服务器安装或 native 测试。

后续从这些分支继续，先完成未接通部分并验证，再推进新 main 节点。
