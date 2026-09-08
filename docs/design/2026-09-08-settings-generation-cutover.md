# Settings generation cutover

版本保持 `1.0.0`。完整切换在 `feat/settings-generation` 分支和[草稿 PR #5](https://github.com/Water-Run/ClashSharp/pull/5) 推进，基础提交为 `e3f597c`。当前已实现迁移、异步设置会话、应用状态流转、代际内服务访问及公共异步入口；生产 composition 仍使用现有设置入口。页面写入、全部运行时参与者和 profile/log/trigger 仓库寿命需要一起接入后，才替换临时架构门禁并合入 main。

## 已实现的存储与迁移

`WindowsLegacySettingsSource` 在读取时才访问 LocalSettings，只请求 registry 的规范键和旧别名。它没有写入接口，未注册字段和控制端凭据不会进入迁移快照。`LegacySettingsSnapshot` 复制允许的不可变原始类型，严格匹配键大小写，限制字符串长度，并为排序后的偏好计算稳定摘要。

`SettingsMigrationPlanner` 将旧整数枚举转换为规范枚举值，验证范围和类型，保留地区模式旧布尔别名及组合模式拆分的实际行为。缺失值采用声明的默认值，非法值采用安全回退。初始 envelope 的 desired 与 key revision 为 1，记录 migration identity 和输入摘要；所有 applied 都是未观察状态，由唯一待办覆盖，迁移本身不声称 Windows 或运行时已应用这些值。

`SettingsAuthorityBootstrapper` 先打开指定代际的仓库，只在已确认空仓库时读取旧偏好。已有 JSON 权威优先；并发初始化采用经过重读验证的获胜提交。损坏、不可用和回执丢失均保留其持久分类，后续会话重新打开实际文件来确定结果。

修复了一个持久化缺口：主文件和备份被隔离为 `.corrupt.<guid>` 后，下一次打开曾将两个原文件名缺失误判为空仓库。仓库现在识别顶层、精确命名的隔离证据，继续返回 Corrupt；有效主文件或备份仍优先处理。两项回归先复现错误的 Succeeded，再验证修复。

## 应用状态与恢复

`SettingsApplicationBatchEditor` 是纯状态编辑器。开始应用时保留 desired 和批次身份，先将该批次记录为 Running，并撤销其旧生效证据。完成需要匹配 generation、batch、attempt，以及完整规范键、类型、值、revision 和摘要；部分结果、旧 attempt 和其他代际的结果不能清除待办。失败保留 Failed 和 unknown applied；重试显式分配新的 attempt identity。

`SettingsAuthoritySession` 在一个代际内串行执行异步修改和应用。持久 Running 确认前允许取消；确认后持有原许可，等待探测、效果及最终保存结束。参与者先探测，目标已生效时直接记录验证；需要应用时使用显式不可变目标，返回后再次探测。应用回执丢失但实际目标已验证时记录 `settings.application.reply_lost_resolved`。探测失败不授权盲目应用，致命异常图继续向上传递。

最终保存失败会使内存投影失效，调用者必须重读仓库。并发文件写入返回真实获胜 envelope，完整保留其他设置修改；Running 待办可在重开后先探测，再决定是否需要效果。会话退休拒绝新命令，并等待已开始的效果和保存完成。

`PrepareStartupAdmittedAsync` 需要独占许可，撤销上一进程留下的 verified applied，创建必要的观察待办。已有 Running 和 Failed 身份继续保留；明确阻止的外部探测保留其处理方式。Restart 批次在 Live 阶段保持待处理，必须由独占启动阶段应用。

## 公共异步入口

`ISettingsAuthority` 与 `GenerationSettingsAuthority` 为页面、磁贴和触发器提供同一条完整命令路径：进入许可、解析当前代际的 `SettingsGenerationContext`、提交完整 desired、依次验证受影响的批次，然后释放代际租约和许可。上下文中的 session 必须匹配租约的完整 descriptor，错误装配在打开其他代际仓库之前被拒绝。快照返回不可变 envelope 和 generation identity，调用者不持有活仓库引用。

复原偏好、失败重试和启动重新观察也经过该入口。多个参与者中后续失败时，前面已验证的证据保留；重试只处理具有新 attempt identity 的失败批次。排队命令在开始前复制输入，外部集合随后变化不会更改已提交的命令内容。

新增回归复现了完整命令的一个衔接缺口：desired 已经提交后，退出开始排空并撤销等待许可，原 session 的普通入口会取消随后的运行时应用。现在 facade 使用内部的已提交命令续行路径，继续验证原许可的有效性，持有原代际，完成全部参与者和保存；后续排队命令仍受撤销控制。直接调用 session 的普通批次入口继续遵守原有 Running 提交前取消规则。

## 代际服务寿命

`DataGenerationManager.ExecuteAsync` 在取得代际租约后，从该代际拥有的 `IServiceProvider` 解析服务，并等待完整操作结束才释放租约。`ReadSnapshot` 仅用于同步、无 I/O 的不可变内存快照，不阻塞异步任务。服务容器的异步释放仍由 `DataGenerationScope` 的原生命周期协议负责。

真实 JSON 仓库与 manifest 集成回归确认：进行中的设置操作阻止切换；提交后只解析新会话，旧会话拒绝写入；回滚恢复原会话并释放候选会话；解析或操作失败释放租约。生产 profile/log/trigger 的容器装配及导入、重置切换尚待完成。

## 本地验证与 CI

- 18 项目 Release x64 完整构建通过，零警告、零错误，用时 26.33 秒。
- 主程序 2705 项通过，零失败、零跳过，用时 49 秒；本分支新增 84 项回归。
- 完整 format 加载 1459 个文件，最终检查零处变更。
- 记录保存在 `artifacts/verification/1.0.0-settings-generation-main.trx`、`build-settings-generation-complete.log`、`format-settings-generation-verified.log` 和 `local-validation-settings-generation-foundation.json`。

基础提交 `2775afe` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34211372829)。实际下载并核验四份 TRX，共 4770 项通过、零失败、零跳过，其中新增 84 项均实际执行。PR 合并提交为 `8febfaf`，其 tree 与 `2775afe` 相同，摘要为 `d325bf6e849ec6edc13602388ae2b8860bc901f4`；收据为 `ci-validation-settings-generation-foundation.json` 和 `ci-settings-generation-foundation-regressions.json`。开发包构建成功，完整生产切换后的候选再执行原生验收。

公共入口追加 9 项回归后，主程序 2714 项全部通过，零失败、零跳过，用时 50 秒；18 项目完整构建零警告、零错误，用时 26.51 秒，format 检查 1464 个文件、零处变更。本分支累计新增 93 项回归。收据为 `local-validation-settings-generation-facade.json`、`1.0.0-settings-facade-main.trx` 及同前缀的构建、格式日志。首次红测有一项许可撤销问题和一项夹具对预建代际目录的错误假设，均保留原报告；错误装配验证以 Settings 目录未创建为实际边界。

持久中断测试使用真实临时仓库、切点注入及新对象重开，运行时参与者为受控模拟。Windows 旧设置适配器已编译，未在开发机读取实际 LocalSettings。实际打包应用的迁移、进程崩溃、完整页面和安装器兼容验收将在生产切换后执行。开发机代理摘要保持 `95e97918ff6de70655b412568cd18dc81c5d6584c607bb9a71ddc72e22460447`。

## 完整切换的剩余依赖

1. 将偏好写入统一为应用层异步 change set；页面、磁贴、触发器和网络提交者使用同一个接口。控制端凭据迁移到独立的内部凭据端口。
2. 为 Internal、Appearance、Network、StartupTask、Sampling、Triggers 实现真实 apply/probe 适配器，明确读取 desired、有效状态和待办的消费者。
3. 在设置驱动的启动步骤之前完成旧事务恢复、代际打开和偏好迁移。profile/log/trigger 与 settings 必须由同一代际容器解析、排空和替换。
4. 将导入、重置和回滚接入候选代际及 manifest 提交，完成生产消费者替换后，原子替换 `SettingsAuthorityArchitectureTests` 中的临时门禁。
5. 运行新候选的 CI、打包应用及隔离 Windows 验收，再将完整节点推送 main。
