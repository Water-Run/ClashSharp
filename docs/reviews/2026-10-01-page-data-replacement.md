# 导入与全量重置的页面接入检查点

此检查点继续私有的数据存储迁移，基于 `c025ea11a8b839851ae26d0540855125699f89e8`。它尚不具备合并到生产 `main` 的条件。共享工作区保持干净，复核时 `main` 仍为 `b70a1538ddd8e722abefa7635b909ffbbedf2b73`，开放 PR 为 0，没有新增命名分支。

此前设置页和磁贴的导入入口、设置页的全量重置仍调用旧设置事务，未使用已经实现的候选数据准备、持久提交和启动恢复流程。本次通过 `ISettingsDataReplacement` 将这些入口接入 `GenerationReplacementCoordinator`，生产视图模型必须提供新的全量重置入口。启动、代理和 TUN 的分组重置仍需迁移，不能据此宣布整体切换完成。

导入现在先刷新设置页或磁贴，再显示完成提示。正常完成、提交后出现警告、需要重启和恢复失败有不同提示；八种语言均补齐文本。重置的警告使用现有 WinUI `InfoBar`。恢复失败保留原始错误、提交状态及重启请求失败，不会再次从视图模型重复请求重启，也不在仍关闭的数据仓库上强制刷新。进程级重启标记不会被随后加载页面的普通偏好状态清除。

切换数据期间，`AppSettingsService` 的只读显示可读取最后发布的不可变设置快照。这个处理只适用于明确的 `Draining` 状态，不开放仓库 I/O、不恢复旧存储写入，也不掩盖对象已释放的错误。成功提交后的读取仍来自新数据目录。

验证结果：

- 最终 Release/x64 全解决方案构建成功，0 警告、0 错误，耗时 33.58 秒。格式检查和 `git diff --check` 通过。
- 使用最终构建产物执行 `dotnet test --no-build --no-restore`：3765 项，3764 通过，1 失败，0 跳过，耗时约 2 分 17 秒。唯一失败仍是 `SettingsAuthorityArchitectureTests.ProductionApp_DoesNotActivateEnvelopeBesideLocalSettingsAuthority`；该整体迁移门禁未删除、跳过或弱化。
- 新增 13 项回归，覆盖真实导入页面操作入口、重置等待和刷新、警告与重启标记、恢复失败及拒绝/异常重启、取消边界、排空期间的快照和释放后的拒绝读取。
- 在蓝色 Windows Server `WIN-07OBU2PV8C6` 复跑这 13 项，全部通过，0 跳过。服务器使用独立临时数据和原生操作替身；这不等于实际 GUI、TUN、系统代理或安装器验收。
- 服务器前后现有 `clashsharp.mihomoservice` 均为 PID 29180、会话 0。本轮未运行本机产品或安装器、未修改本机产品设置，也未升级服务器上运行的服务。
- 最终验证的 27 个源文件/项目/XAML 文件无哈希漂移；5 个关键程序集的本地、冻结记录与服务器 SHA-256 完全一致。

证据目录为 `artifacts/verification/page-data-replacement-20261001`。以 `main-frozen.trx`、`server-results-final/page-replacement.trx`、`test-checks.json`、`checks.json` 和 `new-regressions.json` 为最终结果。早期的编译错误日志和首轮测试结果保留用于追溯。测试项目构建与全解决方案构建产生的程序集哈希不同，因此最终构建后重新打包，并使用同一组冻结文件完成本地全量及服务器复跑；没有将前一批测试结果冒充最终产物的证据。

最终测试包 SHA-256：`2EC3C2CB9D5A436B3DDFB07F9C5BA3A35DAFBD6F23EF678B2C032B9061BAE12B`。服务器证据位于 `C:\ClashSharpValidation\page-data-replacement-20261001-final\results`。

发布前仍须完成分组重置、现有数据目录的普通启动与退出、终端数据维护入口、跨目录触发器编辑版本及页面缓存生命周期的迁移。之后才可替换整体迁移门禁并在蓝色服务器验收配套应用/服务、全部页面和磁贴交互、TUN、安装升级、回滚与卸载流程。本检查点不作为生产就绪声明。
