# 配置恢复后服务启动失败与原恢复重试

同一完整 `85f1a3633f6c51b8bee57fde2e08a03cc8ec9817` 内部候选在指定 Server 2025 上，完成原服务配置已经写入、随后因禁用依赖而启动失败，再撤销测试故障并重试原恢复的实机验收。三个观察点共 **54 项独立后置核对通过**；Windows 服务事件确认实际命中了依赖启动失败，没有修改产品代码。

本轮接续[恢复后主动取消](2026-10-11-original-restoration-cancellation.md)，继续通过 SSH 与原测试账户的 S4U 本地任务执行真实 WPF 命令，没有使用本机 Sandbox。Installer SHA-256 为 `CC11EEE0BB5C45BCE8BE931AA0F1922C3E7A70FA747F56E518D08B3595560FBA`，MSIX 为 `D90DC400299C2FB77ED1ED42CD014D00D9FCCDD52BE952ACCA95DA6150357FE6`。

## 故障建立

开始前服务 Auto/Running、PID 24188，没有公共事务或产品进程。独立核对完整包 288 文件、八个机器文件、原账户 158 文件及备份、服务命令行摘要和 DACL。通过有界准备场景取得公共 `Prepared` 第 1 代、私有 `ContinueCandidate` 第 2 代与 Stopped/Disabled 服务状态；16 项核对通过后释放占用并正常关闭准备安装器。

为当前事务创建唯一的临时禁用依赖 `ClashSharpAcceptanceBlocked-bf8ebcfb1e4e`。其定义、二进制命令摘要和事务绑定先行记录；它始终未启动。先在这项禁用的临时服务上验证依赖字段的设置与清除，再将同样的撤销命令用于故障收尾。服务启动的依赖失败属于 Windows 的标准错误边界，参见 [Microsoft StartService 文档](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew)。

新安装器执行真实“尝试恢复并保留原安装”。独立观察任务等待启动方式恢复为 Automatic，再确认服务仍 Stopped、原命令行与 DACL 已恢复、私有记录为 `PreserveOriginal` 第 3 代，才只修改 ClashSharp 服务的依赖字段。

- 07:54:38.6107183 UTC：首次观察 Automatic 启动方式。
- 07:54:39.2684501 UTC：独立确认原配置和 DACL 已恢复、服务尚未启动。
- 07:54:39.2917177 UTC：准确的单项禁用依赖已写入并重读确认。

日期均为 2026-10-11。观察器没有启动服务、修改包或用户数据，也没有直接改写产品事务。

## 真实失败与重试

Windows System 日志于 07:54:42.1883876 UTC 产生 SCM **事件 7001、记录 101888**，精确匹配 ClashSharp 服务及本次唯一测试依赖，错误参数为 `%%1058`（依赖处于禁用状态）。事件位于故障注入之后、安装器结束之前；原始消息和其他服务事件没有纳入下载材料。

07:55:12 UTC，安装器返回 `installer.machine_helper.response_unconfirmed` 并正常关闭，未宣称恢复完成。独立核对显示服务仍 Stopped/Automatic、PID 0，原命令行和 DACL 已恢复；公共状态仍为 `Prepared` 第 1 代，私有 `PreserveOriginal` 第 3 代保留首次原安装证据。准确测试依赖保持禁用，包、机器文件、用户文件和备份、证书与代理保持；失败阶段 20 项核对通过。

随后测试工具仅清除自己加入的依赖字段。此时服务仍 Stopped/Automatic、PID 0，公共与私有恢复阶段未完成；该动作是撤销测试故障，不计为产品恢复。

重新打开同一安装器并重试保留原安装，07:57:52 UTC 返回 `installer.recovery.original_restored`，显示“已保留原安装”，正常退出码 0。服务 Auto/Running、PID 40484，原命令行、DACL和空依赖列表恢复；公共事务清空，私有 `OriginalClearReady` 第 5 代保留。包的全部 288 文件、八个机器文件、158 用户文件及备份、证书与代理保持，18 项核对通过。

全过程绑定事务 `bf8ebcfb1e4e5d1da2ca776792803db7acdc78451c1ab081fe39bcae54837b1b`，准备与失败时公共摘要为 `6CBA0E9643CD1C3157B97FF7C6EC4573A505BDF7C57B9A32A9DEB3CBC8BD0223`。服务由产品恢复启动，测试工具未代替该恢复步骤。

## 证据与清理

15 份筛选后的 JSON 记录和 `scm-start-summary.json` 摘要索引位于 `artifacts/verification/server-continuation-20261011/`，名称以 `scm-start-` 开头；服务器对应根为 `C:\ClashSharpValidation\continuation-20261011`。用户内容、私有基线与凭据只保留在服务器。

临时依赖服务已按准确名称和定义核验后移除，四项本阶段任务全部完成并撤销，准确证书字节绑定的安装器临时根信任已清理，产品 MSIX TrustedPeople 信任保留。最终服务 Auto/Running、PID 40484，仍归原测试账户，没有公共事务或产品进程。

上一资料提交 `6557daa` 的[CI 38120234587](https://github.com/Water-Run/ClashSharp/actions/runs/38120234587)两项作业成功；当前代码候选已有原始 CI 6,634 项通过，本轮未重复已通过的本地完整集。

本轮覆盖原配置已经应用后的真实依赖启动失败与原恢复重试。它没有覆盖每一种 SCM 配置写入故障、回执丢失、系统重启、标准用户/UAC、远程 Windows 11 或完整网络/页面矩阵；这些范围继续独立推进。
