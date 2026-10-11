# 原安装恢复终态写入失败与后续维护

接续[候选终态首次写入故障](2026-10-11-terminal-write-recovery.md)，同一完整 `85f1a3633f6c51b8bee57fde2e08a03cc8ec9817` 内部候选在指定 Server 2025 上，完成原安装恢复终态写入受阻、重新打开后保留原安装，以及下一次普通修复。四个观察点共 **63 项独立核对全部通过**，没有修改产品代码或载荷。

继续使用 SSH 和原测试账户 S4U 本地任务操作准确签名 Installer 的真实 WPF 控件，没有本机 Sandbox 或新的桌面登录要求。本轮不计入标准用户/UAC 或完整视觉矩阵。

## 故障准备与边界

固定 Installer SHA-256 为 `CC11EEE0BB5C45BCE8BE931AA0F1922C3E7A70FA747F56E518D08B3595560FBA`，MSIX 为 `D90DC400299C2FB77ED1ED42CD014D00D9FCCDD52BE952ACCA95DA6150357FE6`。开始前服务 Auto/Running、PID 34848，原测试账户独占包注册，没有公共事务或产品进程。

除原账户 158 文件及备份、八个机器文件外，本轮还独立冻结实际安装包的完整 **288 文件**摘要与集合；服务命令行只记录摘要，DACL 留作原状态比较。配置、凭据和私有恢复内容保存在服务器保护目录。

只读观察器绑定准确父进程及启动时间、账户、操作、版本和载荷摘要。在公共 `Prepared` 第 1 代持有文件句柄，允许读写共享，禁止删除共享；同一持有句柄再次验证身份和阶段，持有期间具有显式信号与有界退出。没有写入公共日志、调整 ACL 或终止产品进程。

## 真实产品操作与核对

| 观察点 | 产品状态与独立结果 |
| --- | --- |
| 准备完成但阶段替换受阻 | 05:32:24 UTC，界面返回 `installer.machine_helper.response_unconfirmed`。公共事务仍为 `Prepared` 第 1 代，私有记录为 `ContinueCandidate` 第 2 代；服务 Stopped/Disabled、PID 0，准备命令行未变且 DACL 已改变。父进程尚未取得包部署许可；17 项核对通过。 |
| 保留原安装的终态写入受阻 | 重新检查后从真实“尝试恢复并保留原安装”按钮发起恢复。05:33:36 UTC，服务恢复 Auto/Running、PID 31596，原命令行摘要和 DACL 恢复；私有 `OriginalVerified` 第 4 代已落盘，公共事务仍为原 `Prepared` 第 1 代。界面仍报告未确认，16 项核对通过。 |
| 新父进程重试保留原安装 | 显式解除占用，公共日志字节不变，原安装器正常关闭。重新打开同一安装器并选择保留原安装，05:35:30 UTC 返回专用 `installer.recovery.original_restored`，显示“已保留原安装”，正常退出码 0。公共事务清空，私有 `OriginalClearReady` 第 5 代保留，服务 PID 31596 未变；15 项核对通过。 |
| 下一次普通修复 | 新维护事务成功处理旧恢复完成记录，05:37:46 UTC 返回 `installer.completed` 并正常退出码 0。新的私有 `CandidateClearReady` 第 3 代绑定新事务，公共事务清空；15 项核对通过。 |

原恢复事务 ID 为 `1b48531a96815efe909f3c97489bcbee82552fc7fc59a8a15f14b92a5a7ac526`；持有期间公共日志摘要为 `2D4BD13286734136B58A014C7B671A7D774A9DC24D601B1B8C92A20E9BE12390`。两个受阻观察点都由另一进程独立请求删除访问权而不执行删除，确认得到 `ERROR_SHARING_VIOLATION (32)`。释放信号必须绑定同一事务，测试工具没有替产品完成持久化或清理。

下一次修复的事务为 `66d602e0b5b62846ee4f8f1ee4489d9e8f886a5b801cf96b74be6f8aaf95ca37`，服务 PID 为 10060；这是新普通维护的重启，不是原恢复重试中的重复 SCM 操作。

四个观察点均证明原账户文件及备份、八个机器文件、完整包内容、机器/原用户包证书和代理指纹保持。原账户与安装包的文件集合没有新增或缺失。恢复与后续修复后服务命令行摘要、启动方式和 DACL 均与原基线一致。

## 证据与最终状态

12 份筛选后的 JSON 记录、控制脚本与逐文件摘要索引 `original-terminal-summary.json` 位于 `artifacts/verification/server-continuation-20261011/`，服务器对应根为 `C:\ClashSharpValidation\continuation-20261011`。文件名以 `original-terminal-`、`after-original-restoration-repair-` 开头，清理记录为 `original-terminal-write-cleanup.json`。私有基线和用户内容未下载。

三项本阶段任务全部完成并撤销，准确证书字节绑定的临时安装器根信任已移除；产品 MSIX TrustedPeople 信任保留。最终服务 Auto/Running、PID 10060，仍归原测试账户；公共事务、产品进程和本阶段临时任务均不存在，158 文件及备份保持。

本轮补齐原恢复终态首次写入失败后的跨会话重试和下一次维护，没有执行系统重启、恢复中取消、SCM 部分失败或原内容变化拒绝。这些维护范围及完整网络/数据、远程 Windows 11、页面/语言/显示和发行材料继续保留。当前代码候选的原始 CI 为 6,634 项通过；本轮没有重复已通过的本地完整测试集。

上一资料提交 `a3411d6` 的[CI 38114747832](https://github.com/Water-Run/ClashSharp/actions/runs/38114747832)构建/格式/测试与离线安装器打包两项作业均已成功。更早资料提交 `cc80586` 的流水线被后继提交取消，不计为成功。
