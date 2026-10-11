# 修复终态首次写入失败与跨会话继续

同一完整 `85f1a3633f6c51b8bee57fde2e08a03cc8ec9817` 内部候选在指定 Server 2025 上，完成公共 `Verified` 终态首次写入受阻与重新打开安装器后继续同一事务的实机验收。七份脱敏记录与 **28 项独立核对全部通过**；没有修改产品代码、载荷、事务字节或权限。

本次接续[账户往返与主程序启动](2026-10-11-account-roundtrip-and-app-startup.md)，仍使用 SSH 和原测试账户本地 S4U 任务，没有使用本机 Sandbox。Installer 摘要为 `CC11EEE0BB5C45BCE8BE931AA0F1922C3E7A70FA747F56E518D08B3595560FBA`，MSIX 为 `D90DC400299C2FB77ED1ED42CD014D00D9FCCDD52BE952ACCA95DA6150357FE6`。

## 故障与独立观察

普通修复开始前，原账户的 158 文件再次与服务器备份逐字节核对；另外冻结八个机器文件、服务命令行摘要和 DACL。绑定准确父进程及启动时间的受控观察器，只接受同一账户、Repair、版本和载荷摘要的公共事务。

事务进入 `MachineCommitted` 第 4 代时，观察器持有该文件的只读句柄，允许读取和写入共享，禁止删除共享。持有后再次从同一文件句柄核对事务身份和阶段，避免把较早或较晚阶段计入本场景。观察器具有显式释放与超时收尾；没有写入日志、调整 ACL 或终止产品进程。

2026-10-11 05:19:06 UTC，安装器返回 `installer.machine_helper.response_unconfirmed`，界面报告操作状态尚未确认。事务为 `0a312b97b29cb9dab0faeeaf9b173d7ac99ed58ee007ae36511769577dc9ac38`，公共日志仍是第 4 代，SHA-256 为 `0244230EB0B506E3052AC032D6B5F914DDED873728394594431A6834F6A96889`；私有恢复记录为 `ContinueCandidate` 第 2 代。

另一 SSH 观察进程独立请求文件的删除访问权但不执行删除，得到 `ERROR_SHARING_VIOLATION (32)`，确认替换确实被占用阻止。此时服务 Auto/Running、PID 34848；原包、八个机器文件、158 个用户文件及备份、服务命令行/启动方式/DACL、证书和代理均保持。

## 解除占用与产品继续

独立核对通过后，向准确事务发出释放请求。句柄释放前后公共文件摘要相同；释放操作没有替产品完成阶段写入或清理。原安装器经真实关闭按钮正常退出码 0。

随后重新启动同一签名安装器，真实“继续修复”按钮接续同一公共事务。05:21:13 UTC，产品返回 `installer.completed` 并正常关闭，退出码 0。独立核对证明：

- 公共事务清空，私有记录仍绑定原事务并达到 `CandidateClearReady` 第 3 代。
- 服务 PID 34848 在故障、释放和恢复后相同，没有重复执行服务停止/重启；正常修复开始时从旧 PID 33884 变为 34848，不计为恢复期间的重启。
- 原包及主程序集、八个机器文件、158 个用户文件与备份保持，原账户文件集合没有新增或缺失。
- 服务命令行摘要、启动方式和 DACL、机器/原用户包证书及代理指纹保持；应用、内核、看门狗与安装器退出。

持有期间和恢复后各 14 项核对通过。控制脚本的故障场景成功表示“预期未确认状态被观察且安全释放”，不表示受阻期间产品已经完成。

## 证据、清理与范围

证据位于 `artifacts/verification/server-continuation-20261011/`：

- `terminal-write-fault-observed.json`、`terminal-write-held-postconditions.json`
- `terminal-write-failure-ui.json`、`terminal-write-release.json`
- `terminal-write-recovery-ui.json`、`terminal-write-recovered-postconditions.json`
- `terminal-write-cleanup.json`、逐文件摘要索引 `terminal-write-summary.json`

服务器根为 `C:\ClashSharpValidation\continuation-20261011`。私有机器基线、恢复记录、用户配置和备份留在服务器；下载内容只含状态、摘要、静态控件及诊断。两个本阶段任务已完成并撤销，准确证书字节绑定的临时安装器根信任已清理；产品包的 TrustedPeople 信任保留。最终服务 Auto/Running、PID 34848，仍归原测试账户，没有公共事务或本阶段遗留进程。

此前[Verified 后清理失败](2026-10-05-terminal-clear-recovery-acceptance.md)覆盖终态已持久化但无法删除。本次补充终态首次替换失败以及新父进程继续；不扩展为服务部分写入、恢复中取消、清理回执丢失、系统重启或原内容变化拒绝。标准用户、远程 Windows 11、完整页面/网络/数据与发行矩阵继续保留。当前代码候选的原始 CI 为 6,634 项通过，本轮未重复已通过的完整测试集。
