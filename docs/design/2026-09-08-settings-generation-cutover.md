# Settings generation cutover

版本保持 `1.0.0`。完整切换在 `feat/settings-generation` 分支和[草稿 PR #5](https://github.com/Water-Run/ClashSharp/pull/5) 推进，基础提交为 `e3f597c`。当前已实现迁移、异步设置会话、应用状态流转、代际内服务访问、公共异步入口、内部设置运行快照，以及 Appearance、StartupTask、Sampling、Triggers、Network 的服务适配器。控制端凭据已从偏好中拆分并接入生产启动、运行时和数据清理；生产偏好仍使用现有设置入口。Profile 和 Log 仓库已能退休并等待已接收操作结束，但完整代际容器及页面消费者尚未切换。整体接入后，才替换临时架构门禁并合入 main。

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

## 内部设置的只读消费者接口

`InternalSettingsParticipant` 只负责 registry 标记为 Internal 的应用内配置。它用规范默认值创建真实内存配置，不读取存储或迁移偏好，也不将构造视为持久 applied 证据。`IInternalSettingsReader` 仅向消费者提供不可变 `InternalSettingsSnapshot`，没有设置写入和仓库访问能力；完整快照包含所属代际，不含其他参与者的设置。

desired 发布与配置安装分开进行。会话持久保存 Running 后，参与者原子替换本批次涉及的值，保留其他已安装值和独立待办；probe 从安装后的快照读取，不复述请求。旧快照的整组地址或策略值不会随下一次应用改变。取消发生在发布前时保留原快照，发布后没有可遗弃的后台操作或 I/O；丢失回执由实际快照重新验证。

退休阻止新的快照捕获、probe 和 apply，已捕获的历史值及持久数据保持。启动重新观察会识别新消费者的实际默认配置，再安装 durable desired；启动请求要求独占许可。集成测试使用真实 JSON 会话、facade、只读接口和代际管理器，验证代际切换后只解析新实例，原实例已退休，旧磁盘值没有被新实例覆盖。生产消费者的读取端口仍需在整体装配时接入该接口。

## 生产强调色资源的独立验证

原强调色入口在访问 WinUI 资源之前就记录已应用配置，资源不可用或中途写入失败时仍可能向设置页报告成功。两项回归直接调用原主程序程序集，在没有 WinUI Application 的测试进程中复现了跟随系统、自定义颜色都被误报为已应用的问题。

`AccentColorRuntime` 先构造完整不可变资源表，再通过 `IAccentResourceStore` 写入并独立读取所有资源。只有键集合、颜色 ARGB 和资源类型全部匹配，才发布已验证配置。部分写入或无效资源不能证明成功；最后一次写入的回执丢失可由实际完整资源匹配解决。写入后探测暂时失败时，后续读取仍能独立验证已尝试的目标；致命异常图继续传播。

主程序 `AppThemeService` 已使用该路径，配置构造和调色计算没有平台访问。实际生产调色表通过程序集引用测试，覆盖 7 个颜色、41 个画刷、固定 ARGB 混色结果和透明度。透明画刷保持原有 `#00FFFFFF`，与 [Microsoft.UI.Colors.Transparent](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.colors.transparent?view=windows-app-sdk-1.8) 一致。

`WinUiAccentResourceStore` 限制在资源字典所属 UI 线程访问，枚举主字典的局部项，避免将合并字典中的系统资源误当成应用覆盖；跟随系统只移除本服务拥有的覆盖。画刷除颜色外还验证其自身不透明度为 1。资源字典的集合接口及线程关联见 [ResourceDictionary](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.resourcedictionary?view=windows-app-sdk-1.8)。设置页读取实际资源来判断是否尚待应用；主窗口启动步骤也验证目标强调色后才完成。这项修复已接入现有生产设置入口，完整 Appearance 代际参与者和其他外观消费者仍待整体装配。

## Appearance 的完整批次与 UI 操作寿命

`AppearanceSettingsParticipant` 覆盖 registry 中全部十项外观配置。语言、窗口主题和强调色的模式/颜色从 `IAppearanceNativeSettings` 独立观察；托盘、地区显示和磁贴的六项配置由同一代际拥有，通过 `IAppearanceSettingsReader` 暴露不可变已安装快照。构造不访问窗口、资源或偏好存储，快照也不缓存原生 UI 状态。保存 desired 不会改变已安装配置；单键应用从实际已安装值取得配套值，保留其他键的独立待办。

`WinUiAppearanceSettings` 使用注入的实际窗口根元素、语言资源解析器及生产强调色服务，拒绝窗口缺席和错误线程访问。`OwnedUiDispatcher` 将整个同步 UI 回调纳入可等待的操作寿命；排队拒绝不授权其他线程执行，丢失 enqueue 回执时仅撤销未开始的回调。开始后的回调继续完成；窗口寿命结束或代际退休会撤销未开始的工作，并等待已开始的回调结束。主机装配必须将每代独立的操作所有者绑定窗口寿命，并在窗口队列关闭前退休。

原生效果执行后再次读取完整 UI 配置，再原子发布六项消费者策略。最后回执丢失但实际目标完整匹配时可完成发布；部分失败则尝试恢复之前独立观察到的应用内外观，恢复确认后保留失败待办，允许显式重试。恢复也无法验证时保持 unknown，后续探测失败不会盲目重新应用；致命异常图不进入补偿。此恢复能力只替换应用内 UI 配置，不取得偏好写入、网络或系统资源权限。

回归使用真实临时 JSON、facade、代际管理器、生产主程序的完整强调色表及隔离 UI 边界。覆盖全部十键、配套值与其他待办隔离、部分资源写入、失去回执、不可验证补偿、重试、启动重新观察和同一窗口下的代际替换。排队回调测试确认 Running 已持久保存后页面取消仍等待 UI 回调及最后保存，代际切换同时等待这条完整命令。主程序原生适配器也在无窗口进程中直接验证拒绝行为。这些适配器尚未注册进生产代际装配；托盘、磁贴等消费者的读取端口与刷新、正常窗口的实际交互仍在整体切换时接入和验收。

## StartupTask 与 Sampling 的实际服务适配

`StartupTaskSettingsParticipant` 和 `SamplingSettingsParticipant` 在访问运行时之前检查完整 generation descriptor、应用类别、允许的键和原许可的有效性。两者都不写偏好、不重新申请普通许可。StartupTask 通过生产 `StartupLaunchService` 读取 Windows 注册状态；已满足目标时不重复注册，拒绝或未知状态保留待办。应用回执丢失由后续独立平台探测判断。

`ConnectionSamplingService` 现在串行拥有完整的配置与生命周期转换。运行中的循环使用已经安装的间隔；偏好变化不会在下一轮采样中提前生效。显式配置入口等待旧循环及未完成的采样结束，再同时安装启用状态和间隔，随后启动新循环。该入口开始排空后不会因页面取消而放弃任务。探测读取实际循环和已安装的间隔，不访问旧偏好；永久停止的服务不能被报告为已启用。只修改一个采样键时，适配器从同一不可变请求读取配套值，暂停、恢复及重新启用也使用该完整配置。

新增回归使用真实 JSON 会话、生产启动服务和采样 supervisor，Windows、mihomo 及统计存储边界采用隔离模拟。测试覆盖启用、禁用、拒绝后显式重试、回执丢失、错误代际和许可、单键修改、旧循环排空与退出许可关闭，以及尚未应用的 desired 不改变实际间隔。它们没有修改开发机启动任务或访问实际 mihomo。

另外修复了启动服务的异常分类：探测和设置入口先检查完整异常图，嵌套致命异常保持原异常传播，不能返回未知状态或包装成 `StartupLaunchUpdateException`。两项回归先复现旧行为，再验证修复；持久 Running 记录保留给后续进程重新观察。

## Triggers 的调度与通知配置

`TriggersSettingsParticipant` 拥有实际 `TriggerScheduler`，调度循环和生产 `TriggerFiredNotificationAdapter` 共同读取 `TriggerSettingsState` 的同一份不可变已安装配置。它覆盖 registry 中的总开关和通知开关，应用后独立探测两者；保存 desired 不会提前改变调度或通知。单键批次保留另一个键的实际值及其独立待办，混合批次完整验证。状态对象检查所属代际，退休后拒绝继续读取。

构造不启动任务或访问存储。主机需要先初始化 trigger 仓库及 outbox，再启动该适配器拥有的 scheduler，最后接入设置应用。未初始化、暂停或停止的循环不能被报告为已应用；显式应用排空当前和排队的评估后安装配置，再恢复同一循环。禁用评估时仍保持维护循环，以便继续重试生命周期释放确认；调度器静默不等于所有持久确认都已完成。开始排空后，页面取消不会截断操作；释放适配器等待实际循环停止后撤销共享配置。

回归复现了设置入口与调度任务互相等待的路径：UI 持有完整设置命令入口并等待调度器退出，而调度任务也在等待该入口。facade 现在先取得独占许可并排空普通调用，再进入命令入口。总开关、通知开关及其复原均采用该顺序；重试在独占许可内解析当前批次，避免预读身份与执行之间的竞态。已持有普通许可的触发器设置写入会在 desired 发布前被拒绝，其他类别的已提交命令仍完成既有续行协议。

测试直接调用主程序程序集中的实际调度适配器和通知适配器，平台、事件、时钟和通知投递边界使用隔离模拟。覆盖排空及取消、丢失回执、恢复失败与显式重试、致命异常图、通知策略、跨代际拒绝、旧实例释放及完整批次。首次完整检查发现测试源码重复编译违反已有 trigger 架构约束，已改为程序集引用，保留原约束；补齐通知键时的两项失败及夹具修正记录也保留。这些适配器尚未注册进生产代际装配。

## 独立控制端凭据

`AppSettingsService` 及其 editor 不再生成、读取或删除控制端凭据，核心配置偏好端口也不再携带 secret。`IControllerCredentialProvider` 只读取启动时已验证的进程凭据；HTTP、WebSocket 和配置生成使用同一提供者，独占设置操作期间的读取不访问存储、不获取新许可。生产主机拥有 `ControllerCredentialService`，现有静态运行时工厂通过显式启动绑定访问它。

`WindowsControllerCredentialStore` 只访问原来的 `MihomoControllerSecret` 槽位，不枚举偏好或私有未知值。存在且合法的旧凭据原样保留；缺失或类型、格式非法时生成 256 位随机凭据，写入后独立重读一致才发布。存储不可用或结果无法验证时阻止启动，不产生临时内存替代凭据；丢失写入回执但实际值已经匹配时完成初始化。异常只对外报告稳定代码，致命异常图继续传播。此 Windows API 使用主程序的包身份，符合[系统管理应用数据的适用范围](https://learn.microsoft.com/en-us/windows/apps/develop/data/store-and-retrieve-app-data)。

新增启动步骤顺序为 140，位于 Installer 事务门禁（125）之后、运行时恢复（150）之前。构造过程没有存储副作用，初始化失败时不绑定运行时消费者。主机释放会撤销进程中的可用凭据投影。

普通偏好重置保留凭据。清除全部数据先停止运行时，再通过独立维护能力删除并确认槽位不存在，随后清理其他数据。进入终态后使用 shutdown maintenance 许可；偏好删除已经开始时，页面取消不会中断后续凭据和文件清理。凭据删除无法确认会使清理失败并停止后续文件删除，避免声称已全部清除。凭据服务的释放发生在主机停止及数据维护之后。

原有 `launch-no-proxy` 验收只确认包身份、窗口存在及稳定时长；启动错误页也可能满足该条件。现补充读取候选本次启动之后的 SQLite 日志聚合，要求 controller-credential（140）、window-shell（600）和最后的 profile-subscription-updates（710）各完成一次且成功，并且没有启动错误。查询使用系统 SQLite 只读连接，不读取凭据值、不输出日志内容，结果时间范围绑定实际 launch 步骤。PowerShell 5.1 与 7 均通过 111 项报告断言和 16 项真实隔离 SQLite 断言，覆盖错误页、未完成流程、重复或过期记录、锁定及损坏数据库；日志为 `startup-evidence-powershell51.log` 和 `startup-evidence-powershell7.log`。这些检查仍不代表全部页面交互或正常安装器流程已验收。

当前生产装配已接入这项拆分；JSON 偏好权威和数据代际整体切换仍未激活。该凭据候选已通过下述实际打包启动验收。

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

公共入口提交 `a08fc4b` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34214328575)，实际下载的四份 TRX 共 4779 项通过，零失败、零跳过。合并提交 `e31dd2c` 与源提交的 tree 同为 `78fe7a3dd89e81d4ba3203ca5dbb16ab4d6b4e35`，验证收据为 `ci-validation-settings-generation-facade.json`。开发安装器包构建成功，尚未为这份包追加原生验收。

实际服务适配和启动异常修复新增 16 项回归，本分支累计新增 109 项。最终完整主程序 2730 项通过，零失败、零跳过，用时 51 秒；18 项目 Release x64 构建零警告、零错误，用时 22.29 秒，format 检查 1473 个文件、零处变更。收据为 `local-validation-settings-generation-runtime.json`、`1.0.0-settings-runtime-final.trx`、`build-settings-runtime-final.log` 和 `format-settings-runtime-verified.log`。两项异常图红测保存在 `1.0.0-settings-runtime-fatal-red.trx`；此前夹具对初始 revision 和匹配 applied/pending 的错误构造也保留独立失败报告，不计为产品缺陷复现。

运行时适配提交 `37f4c51` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34217135381)，实际四份 TRX 共 4795 项通过、零失败、零跳过。合并提交 `95ee8d8` 与源提交的 tree 同为 `a85ebf2b51881d3a38f3e9321100edec68c4b014`，收据为 `ci-validation-settings-generation-runtime.json`。安装器包构建成功，此份包仅核验构建结果及元数据。

凭据拆分验证包含 30 项新用例，并将原先设置类中的凭据删除回归替换为独立数据维护职责的检查，净增 29 项。本分支主程序累计净增 138 项，完整 2759 项通过，零失败、零跳过，用时 52 秒；18 项目构建零警告、零错误，用时 27.21 秒，format 检查 1488 个文件、零处变更。定向 99 项通过；收据为 `local-validation-controller-credentials.json`、`1.0.0-controller-credentials-main.trx`、`build-controller-credentials-complete.log` 和 `format-controller-credentials-verified.log`。首次定向验证的退出夹具重复提交已经终态的许可，调整为可提交退出的独占许可后通过，原报告保留。

凭据拆分提交 `e9026f8` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34221242635)，实际四份 TRX 共 4824 项通过、零失败、零跳过，30 项新凭据用例均核验身份并实际执行。合并提交 `46154b7` 与源提交 tree 同为 `769cfda3cc5b10234366a11dd6e24102e1bc4ab0`，收据为 `ci-validation-controller-credentials.json`。开发安装器制品 `10054117859` 共 317646418 字节、8 个文件，安装器版本为 `1.0.0+46154b776b2fbb501a388bee6a3f484905353f96`。整包请求三次返回存储端 `OperationTimedOut` 后，以分段请求取得完整 ZIP，其 SHA-256 与 CI 元数据一致；收据为 `installer-artifact-controller-credentials.json`。

实际 Windows Sandbox 验收使用上述 CI 候选和 `f5a4502` 的验收脚本，二者之间没有程序代码变更。MSIX SHA-256 为 `653c839f2a5c036e6e00622a7f6a1986ed1bcec8b52238e253702849f1a4458c`；运行 `10d6f352c4c3452791e170ff7a32f13f` 在 2026-09-08 11:56 UTC 通过全部 12 步。实际包身份与进程匹配，窗口稳定 30247 毫秒，凭据、交互主窗和最后启动步骤各成功一次，启动失败记录为零。7 项来宾清理均成功，沙箱 `3f577a24-d645-4158-a3f1-99ecb3d4f195` 已销毁，输入及主机代理保持不变；收据为 `sandbox-package-validation-controller-credentials.json`。此运行没有调用正常 WPF 安装器，不代表完整页面交互或优雅退出验收。

验收脚本提交 `f5a4502` 的 CI 安装器构建成功，主程序 2758 项通过、1 项失败：仓库规范检查发现两个 PowerShell 辅助函数缺少 `.DESCRIPTION`。已补齐说明，本地 16 项仓库规范测试全部通过；报告为 `1.0.0-startup-evidence-topology.trx`。原日志和制品保存在 `ci-startup-evidence-failed.log` 与 `ci-tests-startup-evidence-initial.zip`，收据为 `ci-validation-startup-evidence-initial.json`；该失败不涉及程序运行行为。实际 CI 的两版 PowerShell 也各通过 111 项报告及 16 项 SQLite 断言。远端绿色载荷复验的 SSH 在握手阶段关闭，未上传此候选，原有远端 main 仍为 `e3f597c`。

持久中断测试使用真实临时仓库、切点注入及新对象重开，运行时参与者为受控模拟。Windows 旧设置适配器已编译，未在开发机读取实际 LocalSettings。实际打包应用的迁移、进程崩溃、完整页面和安装器兼容验收将在生产切换后执行。开发机代理摘要保持 `95e97918ff6de70655b412568cd18dc81c5d6584c607bb9a71ddc72e22460447`。

脚本说明修复 `3dbbb4e` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34224586524)，实际四份 TRX 共 4824 项通过、零失败、零跳过。合并提交 `f3ce2e6` 与源提交 tree 同为 `ecd542df0c693a7f74cc3384b7dda2b25013dbe3`，收据为 `ci-validation-startup-help.json`。该次安装器包仅核验构建及元数据，原生启动证据仍绑定上述凭据候选。

Triggers 适配和入口顺序修复新增 22 项回归，本分支累计净增 160 项。完整主程序 2781 项全部通过，零失败、零跳过，用时 56 秒；18 项目 Release x64 构建零警告、零错误，用时 26.04 秒，format 检查 1491 个文件、零处变更。收据为 `local-validation-trigger-settings.json`，最终报告为 `1.0.0-trigger-settings-both-keys.trx`、`build-trigger-settings-both-keys.log` 和 `format-trigger-settings-both-keys-verified.log`。死锁复现保存在 `1.0.0-trigger-settings-cycle-red.trx`；最初通知键遗漏、程序集引用问题及夹具错误报告分别保留，不将先前未完成的验证累计为通过项。

触发器提交 `9a331ba` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34229757710)，实际四份 TRX 共 4846 项通过、零失败、零跳过，22 项新增用例逐一匹配本地身份。合并提交 `ad5a3b1` 与源提交 tree 同为 `9de5e93966d08e2174e15ec8260a103e1e2ec661`，收据为 `ci-validation-trigger-settings.json`。开发安装器归档 `10057617117` 共 317653478 字节，SHA-256 为 `234b68e19d297c45b0470ccaece0bbe89fb549139407ee2a8124cc4c15fa3033`；该候选只核验构建与元数据，尚未对这组未装配的适配器追加原生运行验收。

内部设置只读接口与实际配置所有者追加 10 项回归，本分支累计净增 170 项。完整主程序 2791 项全部通过，零失败、零跳过，用时 58 秒；18 项目 Release x64 构建零警告、零错误，用时 26.06 秒，format 检查 1495 个文件、零处变更。收据为 `local-validation-internal-settings.json`，最终报告为 `1.0.0-internal-settings-main.trx`、`build-internal-settings-complete.log` 和 `format-internal-settings-verified.log`。首次编译修正两处测试断言分析器用法；初轮测试的三个失败来自夹具对预建目录及 URL 规范化的错误预期，报告保留于 `1.0.0-internal-settings-components-final.trx`，不计作产品缺陷复现。

内部设置提交 `04ded00` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34231353788)，实际四份 TRX 共 4856 项通过、零失败、零跳过，10 项新增用例全部匹配本地身份。合并提交 `0edd44c` 与源提交 tree 同为 `9c766820a60a2aaf974b4dd03ffa4716344b2b8c`，收据为 `ci-validation-internal-settings.json`。开发安装器包构建成功，本次只核验制品元数据。

强调色验证修复追加 22 项回归，本分支累计净增 192 项。完整主程序 2813 项全部通过、零失败、零跳过，用时 57 秒；18 项目 Release x64 构建零警告、零错误，用时 29.06 秒，format 检查 1502 文件、零处变更。收据为 `local-validation-accent-runtime.json`，最终报告为 `1.0.0-accent-production-main.trx`、`build-accent-production-complete.log` 和 `format-accent-production-verified.log`。旧实现的两项失败保存在 `1.0.0-accent-unavailable-red.trx`，编译后已恢复并核对工作文件摘要；初次编译的命名及异常构造分析器诊断也保留原日志。完整自定义资源表及故障注入使用隔离资源存储边界，默认跟随系统与新启动检查的原生证据如下。

强调色提交 `a4f6368` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34236028856)，实际四份 TRX 共 4878 项通过、零失败、零跳过，22 项新增用例全部核对。合并提交 `6b0d9e3` 与源提交 tree 同为 `1115ea1ac545ee6197a6343cddf23867170418d3`，收据为 `ci-validation-accent-runtime.json`。已下载并验证安装器制品 `10060250945` 的全部 8 个文件；归档共 317688931 字节，SHA-256 为 `b816bde0ef20921ab50e1f89c73936eaab733a407a02375d5a70ae88af9fb193`，安装器版本为 `1.0.0+6b0d9e313394a363e52878bfc4832b17023c613f`。收据为 `installer-artifact-accent-runtime.json`。

同一源提交和验收脚本在 2026-09-08 14:18 UTC 完成实际 Windows Sandbox 运行 `9328bd3fefce42b7a3ed7b70dba422b8`。MSIX SHA-256 为 `20da82391439e06ce0877a5febdeb56c4e1af5be9f0d671afb725810e21bfb22`；12 步全部通过，实际包进程与 EXE 摘要匹配，主窗口稳定 30366 毫秒，凭据、主窗口及最后启动步骤各成功一次，启动失败记录为零。7 项来宾清理成功，沙箱 `4f30c13b-672d-4ac5-b113-fc825f429f49` 已销毁，输入和主机代理不变；收据为 `sandbox-package-validation-accent-runtime.json`。这验证了新资源检查下的默认启动，未执行自定义配色的实际页面操作、正常 WPF 安装器流程或优雅退出。

验收记录提交 `ff0e065` 的[两项 CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34237732342)，实际四份 TRX 共 4878 项通过、零失败、零跳过，22 项强调色用例全部核对。合并提交 `35a11c6` 与源提交 tree 同为 `5fcf949bc9d2d71ef07cf2af64f312c31f4ae91a`，收据为 `ci-validation-accent-evidence.json`。该提交只有文档变化，安装器仅核验构建及元数据；原生证据继续绑定上述 `a4f6368` 候选。

Appearance 参与者及 UI 操作所有者新增 26 项回归，本分支累计净增 218 项。完整主程序 2839 项全部通过、零失败、零跳过，用时 59 秒；18 项目 Release x64 构建零警告、零错误，用时 27.66 秒，format 检查 1510 文件、零处变更。收据为 `local-validation-appearance-runtime.json`，报告为 `1.0.0-appearance-runtime-main.trx`、`build-appearance-runtime-complete.log` 和 `format-appearance-runtime-verified.log`。初次定向编译修正了一处异步异常断言的分析器用法，原日志 `test-appearance-dispatcher-components.log` 保留；此前两个定向集合分别通过 22 和 26 项。该节点没有激活新的生产设置权威，完整装配后的 UI 与安装器候选仍需原生验收。

## 完整切换的剩余依赖

1. 将偏好写入统一为应用层异步 change set；页面、磁贴、触发器和网络提交者使用同一个接口。独立控制端凭据已接入生产调用，后续代际重置继续使用该能力。
2. 将已实现的 Network、Appearance、Internal、StartupTask、Sampling、Triggers 一起装配；先恢复启动时的运行时归属，再执行真实观察，不能用 desired 推定 applied。明确读取 desired、有效状态和待办的消费者，并接通外观变化后的页面刷新。
3. 在设置驱动的启动步骤之前完成旧事务恢复、代际打开和偏好迁移。profile/log/trigger 与 settings 必须由同一代际容器解析、排空和替换。
4. 将导入、重置和回滚接入候选代际及 manifest 提交，完成生产消费者替换后，原子替换 `SettingsAuthorityArchitectureTests` 中的临时门禁。
5. 运行新候选的 CI、打包应用及隔离 Windows 验收，再将完整节点推送 main。

2026-09-12 的 `4640685` 已补齐 Network 四键批次，观察实际配置 generation/hash、SCM 会话、认证控制端以及完整 Windows 代理状态和自有 journal；只有独立观察到上一状态或目标状态，才允许显式重试。Profile 和 Log 的每个已接受操作持有寿命租约到异步工作及补偿结束，退休排空后再释放存储。它们尚不构成生产 JSON 权威切换。

该源码的 CI 5012 项全部通过，完整服务器新包通过实际安装、启动日志及窗口、修复、WPF 窗口和卸载验证。实测修复了默认 DIRECT 循环及配置文件瞬时替换失败；这两项和安装器改进已独立移植到 main `db21085`。新包实测、源码分支和未完成项的准确边界见 [Windows 实机开发与验收记录](../reviews/2026-09-12-server-acceptance.md)。
