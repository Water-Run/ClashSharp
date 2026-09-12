# 2026-09-12 Windows 实机开发与验收记录

本记录区分已取得的运行证据、源码修复和待复验事项。目标版本为 **1.0.0**，当前尚不能认定完整开发或正式发行验收完成。后续状态同步到 [1.0.0 执行账本](1.0.0-execution-ledger.md)。

验收环境为 **Windows Server 2025 Desktop Experience x64**。基线源码为 `ea940a140775636ac78952a01ef6d7d866c391b0`；基线 MSIX 的 SHA-256 为 `ecc83c1d72bb1f98d65c8061b0f0993a7b23393fdcb5e73ecedfc34d6317ac50`。该包使用临时测试签名，供内部验收，不是正式发行包。来源为本地证据 `artifacts/verification/server-acceptance-initial-20260912.json`；公开记录不包含机器连接信息、用户路径或私钥材料。初始快照保留当时的清理待办，后续最终清理结果记录于下文新包验收收据。

| 项目 | 已取得证据 | 验收边界 |
|---|---|---|
| 安装、修复、卸载 | 真实 Windows native 安装引擎依次完成 install / repair / uninstall，均返回 Succeeded | 并非仅模拟平台测试；也不等同于完整 WPF 页面点击验收 |
| 卸载后资源 | 包注册、服务、产品运行文件及安装事务管理的证书均无残留；预装依赖保留，代理指纹前后相同 | 存在下述空目录残留；临时验收签名材料与产品卸载资源分开记录 |
| WPF 窗口 | 正常窗口启动并稳定运行 30 秒 | 尚未执行完整按钮点击、交互与取消流程的实机验收 |
| WinUI 首次启动 | 基线包在启动步骤 450 出现 Fatal，诊断为 `configuration.rejected` | 基线首次启动未通过 |
| 默认配置修复 | 原配置交给实际随包 core 执行 `-t` 返回 exit 1，报出 DIRECT 循环；修复后在远端使用完全相同的 core 执行 `-t` 返回 exit 0 | 后续完整新包的正常首次启动也已通过，见下文 `4640685` 验收 |

首次启动的根因为 [MihomoRuntimeConfigurationBuilder](../../ClashSharp/ClashSharp/Service/MihomoRuntimeConfigurationBuilder.cs) 额外声明了名为 `DIRECT` 的 select group，并让该组引用 `DIRECT` 自身。修复删除这四行声明，继续使用 core 内建的 `DIRECT`。六项真实 core 默认配置校验回归已通过；独立 `-t` 与完整包启动分别验收，不能替代页面交互证据。

当前生产设置仍以 **LocalSettings 为唯一权威来源**。JSON generation、desired/applied 流程和 Network 等 participants 已有实现与隔离测试，但尚未完整接入生产主机。完成接线前仍需明确启动时运行时 owner 的恢复和 reconcile，不能把期望设置当成已应用状态。

第一轮主体测试共 **2876 项，2875 通过、1 项失败**。失败项为 `RuntimeConfigurationTransactionTests.ApplyRuntimeConfigurationAsync_RepeatedSuccess_RetainsOnlyBoundedVerifiedSnapshots`，当次断言 expected 8、actual 7。增强每轮事务结果诊断后，第二轮完整回归 **2899 项，2898 通过、1 项失败**，另一配置导入用例在 `File.Move(overwrite: true)` 返回 `UnauthorizedAccessException`；两个相关 IO 用例的有限复跑在第二轮再次捕获状态清单替换被拒绝、事务成功回滚。尚无证据将其归因于特定外部软件。

修复只针对核心配置的同目录文件提交：对 Windows 原生访问拒绝、共享冲突和锁冲突最多重试五次，每次请求等待 20 ms；不重复外围事务，不修改权限或只读属性，持续失败保留首次异常并沿用回滚。异步等待可取消，进程致命异常不重试。此边界不代表既有外围所有异常处理都已重构。新增十六项注入故障与真实只读文件回归，并纳入下述统一验证。

统一回归所测源码已整理为 `cb378e5`（安装器）、`10dc4e4`（默认启动及文件提交）、`4640685`（网络状态观察及仓库退出等待）。本机主程序 **2915/2915**、安装器 Core **979/979**、Presentation **129/129**、Windows 安全套 **983/983** 全部通过，合计 **5006**，零失败、零跳过；Windows 的六项真实证书修改用例按准确类名排除，留给隔离 CI，不能计入本机结果。原失败的两个 IO 用例另连续复跑十轮，20/20 次通过，不重复累计入全套数量。

格式检查覆盖 1522 个文件，零处变更。PowerShell 5.1 与 7 均解析 21 个源文件，无语法错误；安装器构建配置与时间戳地址的 25 项检查通过。完整日志和 TRX 保存在 `artifacts/verification`，包括 `1.0.0-server-readiness-final-main.trx` 与 `installer-final-20260912`。新完整包从准确提交 `4640685f6a80d3d4396f93b0cd7bac65e20ba379` 独立构建。

18 项目 Release x64 解决方案构建通过，零警告、零错误。开发分支 `4640685` 的[两项 CI 均已成功](https://github.com/Water-Run/ClashSharp/actions/runs/34686949527)。下载测试归档并核对其 SHA-256 后，四份实际 TRX 为主程序 2915、Core 979、Presentation 129、Windows 989，合计 **5012/5012**，零失败、零跳过，包含六项隔离证书测试。收据为 `artifacts/verification/ci-server-readiness-4640685-counts.json`。

服务器从准确 `4640685` 提交独立构建了完整新包，构建前后源码均干净，原仓库及旧源码目录保持。MSIX 为 **192630568 字节**，SHA-256 **`af4e73f60fc53e5950b92ea1bcf6413e14569966727ee47d4e77324bd27a342a`**；构建及输出摘要见 `artifacts/verification/server-source-build-4640685.json`。验收候选使用现有服务器测试签名，生产安装引擎和 helper 按相同 EXE 身份运行，未放宽验证规则。

新包的实际安装与修复均返回 `installer.completed`，无待恢复操作。普通 WinUI 进程通过新源码收据、manifest、MSIX 内 DLL 和实际安装 DLL 的摘要绑定；启动 140、600、710 各成功一次，错误数为 0，主窗口稳定 **30260 ms**，完整代理指纹前后相同。观察脚本没有操作页面。为了继续修复及卸载验收，随后按准确 PID、创建时间、路径和 EXE 摘要结束自有测试进程，该动作明确记录为强制结束，不算正常退出验收。

安装器和启动修复已独立移植并推送 **main `db21085`**，包含 `888cc6e`、`02ce053` 和只修复 Git worktree 测试定位的 `db21085`。独立 main 工作树完整本机回归 **4734/4734**（主程序 2643、Core 979、Presentation 129、Windows 安全集 983），零失败、零跳过；锁定还原、18 项目 Release 构建和 1443 文件格式检查通过，锁文件未变。[main CI 两项任务均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34687600298)，实际下载的四份 TRX 共 **4740/4740**，包含六项隔离证书测试，零失败、零跳过。测试制品 `10296365906` 为 1492297 字节，SHA-256 `4922f0285a8d19243a73b05fd6af001ec1de7e5d9266ea32f4a4a74821c77c23`，与 GitHub 摘要一致。这份 main 源码验证和上述 `4640685` 服务器完整包验证分别记录，不混用包摘要。

新版 WPF 候选已完成实际发布、有效测试签名、时间戳校验及 `--verify-payload`，核对 4 个外部文件、7 个机器文件，载荷与完整源码包一致。EXE 为 **90736304 字节**，SHA-256 为 **`5ac54526f2a684430104b612a922e2f4f7d29bdd31072cee784d99cfbee317e3`**；正常窗口在桌面会话中稳定 **30085 ms**。没有执行完整按钮点击或取消交互；随后为卸载测试按准确进程身份强制结束窗口，未计为正常关闭。

新包真实卸载返回成功，复核程序包、服务和测试程序进程均为 0，自有机器 TrustedPeople 证书已移除，原有依赖与既有 MSIX 签名私钥保留，代理指纹保持。卸载仍留有下述六层空目录，已由测试收尾按初始不存在、路径无重解析点且为空逐层清除，**这不属于安装器自动清理功能**。本轮五个任务已移除；临时 EXE 测试签名的 Root 信任、My 证书及 CNG 私钥均已删除并复核，公开验收证据保留。

最终归档为 `artifacts/verification/server-acceptance-fixed-4640685.zip`（27489 字节，SHA-256 `66b56a5955d5d984e3d1a4c822f9d0ae106bb29266fab980f7657a97e0ac0d24`），脱敏索引为同目录 `server-acceptance-fixed-4640685.json`。**完整页面交互、正常退出和自动空目录清理仍未验收完成。**

空目录自动清理已进入后续独立开发批次，尚未完成产品实现。初始环境中不存在、卸载后仍为空的目录包括 `%ProgramFiles%\ClashSharp`，以及 `%ProgramData%\ClashSharp` 下的 `Installer\v2`、`InstallerAuthority\v1` 及其空父目录。现有清理只覆盖 `Service`、`MihomoService` 叶目录；事务与证书存储只删除文件。

- `%ProgramFiles%\ClashSharp` 需在机器部署 guard 释放后，补充父目录归属与为空验证。
- ProgramData 中的事务目录、私有 authority 目录及公共父目录需要改变清理生命周期：父进程只读事务 reader 当前长期持有禁止删除共享的目录句柄，helper 的存储也持有保护句柄。不能直接在现有删除循环中追加路径。
- 拟在成功卸载的 `Clear` 清除 journal 前保存受保护的卸载终态检查点，清除并复核 journal 后、成功回复前完成目录清理：父进程 reader 每次读取完成即释放观察句柄；helper 释放存储句柄后仍持有独占 authority/application lease，再按固定路径清单由子到父清理、复核，最后确认成功。中断后的恢复不能只以 journal 缺失推断完成。该方案尚未实施。
- 删除前必须验证完整路径链、无 reparse、对应目录归属及为空；公共 `ClashSharp` 祖先现有的 rename-anchor 验证不足以直接授权删除。保留非空或归属不能确认的目录，不递归删除，不放松 handle/owner 保护，不为清理修改 ACL。后续须补句柄释放顺序、外部条目保留、IO/权限失败和成功回复丢失的回归。

空目录当前不包含运行文件、服务、证书或用户数据，不阻断本轮首次启动修复；它们仍是“彻底清理”验收的未完成项。
