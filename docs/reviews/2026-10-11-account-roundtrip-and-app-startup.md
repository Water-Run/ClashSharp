# 当前候选的账户往返与主程序启动

接续[当前候选生命周期](2026-10-11-current-candidate-lifecycle.md)，继续通过指定服务器的 SSH 调度本地任务，使用准确 `85f1a3633f6c51b8bee57fde2e08a03cc8ec9817` 完整内部签名候选。没有使用本机 Sandbox，也没有要求用户重新登录远程桌面。服务器为 Server 2025；现有 Administrator 交互会话用于正常 WinUI 注册激活，原测试账户的 S4U 本地任务用于安装器操作。

## 候选与执行范围

本轮使用同一 Installer `CC11EEE0BB5C45BCE8BE931AA0F1922C3E7A70FA747F56E518D08B3595560FBA` 和主 MSIX `D90DC400299C2FB77ED1ED42CD014D00D9FCCDD52BE952ACCA95DA6150357FE6`；两账户实际安装的主程序集均匹配 `18B8C3F1A10AA76724297520E399982A65BC4E03063A7BD095297CB31EE8DB10`。没有新增产品代码或替换载荷。

WPF 操作通过准确进程的真实 UI Automation 控件发起，经过产品确认页与生产父进程/helper。主程序使用正常 `IApplicationActivationManager.ActivateApplication` 注册激活；没有直接绕过包入口或使用调试激活。S4U 任务的 `RunLevel Limited` 不证明令牌未提升，本轮不计入标准用户或 UAC 验收。

## 账户往返与旧副本卸载

| 场景 | 实际结果与独立核对 |
| --- | --- |
| 原测试账户 → Administrator | 专用切换入口及“确认切换使用账户并继续安装”完成；服务关联转为 Administrator，原包注册与原账户 158 文件保持，新账户包注册及主程序集摘要匹配，公共事务清空。 |
| Administrator → 原测试账户 | 同一产品切换入口完成；服务重新归属原测试账户，两个账户注册保持，原账户 158 文件与 Administrator 25 文件逐字节保持，公共事务清空。 |
| 卸载已退役的 Administrator 副本 | 使用专用“卸载此账户的 ClashSharp 副本或继续卸载”及确认页，返回 `installer.completed`，安装器正常退出码 0；只剩原测试账户注册，主程序集保持，服务 Auto/Running、PID 33884 不变，服务关联文件摘要不变。 |

最后一项独立核对共 18 项全部通过，包含原数据与三组备份、两个账户代理指纹、包及服务归属、事务和进程状态。机器与原账户的当前 MSIX TrustedPeople 证书保留，Administrator 的产品拥有证书被清除。Administrator 切换前已有的一份外部文件保持；启动后生成的 25 文件备份留在服务器保护目录。

原账户文件恢复来自上一阶段显式备份回填，本轮账户切换验证其保留，不能据此声称已验证历史版本的自动数据迁移。

## WinUI 启动、设置页与正常退出

切换到 Administrator 后，在已有交互会话 2 正常激活注册应用，返回成功并显示 `Clash#` 主窗口。真实 `NavSettingsItem` 导航到设置页，再由 `ExitApplicationButton` 执行正常退出。只记录仓库本地化静态文本与控件身份，未读取用户配置或流量内容。

首次运行的控制脚本通过后取得的 .NET `Process` 没有返回退出码，因此保留其失败收据；窗口退出和进程消失本身不替代退出码验证。随后修正验收脚本，在发出退出命令前独立持有原生进程句柄。重开、设置页导航、正常退出再次完成，`GetExitCodeProcess` 确认 **退出码 0**，内核及看门狗不存在，代理指纹保持。

首次运行的启动诊断包含全部 17 个步骤的成功终态，生命周期错误数为 0；旧日志库及当前数据代日志库的 SQLite 完整性均为 `ok`。数据代迁移复制的早期日志不重复计为多次启动。这里验证启动、导航和退出，不扩展为所有页面、网络模式或视觉验收。

## 证据与最终状态

证据根为 `artifacts/verification/server-continuation-20261011/`，服务器对应 `C:\ClashSharpValidation\continuation-20261011`。新增下载的 11 份脱敏记录包括九份成功收据、一份保留的首轮退出码夹具失败，以及只含静态启动诊断的提取。私有数据备份、配置与凭据留在服务器。

- `owner-transfer-to-admin-ui.json`、`owner-transfer-to-admin-postconditions.json`
- `admin-first-startup-fixture.json`、`admin-startup-diagnostics.json`
- `admin-reopen-exit-ui.json`、`admin-reopen-exit-postconditions.json`
- `owner-transfer-back-csh-ui.json`、`owner-transfer-back-csh-postconditions.json`
- `retired-admin-uninstall-ui.json`、`retired-admin-uninstall-postconditions.json`
- `account-roundtrip-cleanup.json`

所有本阶段任务已完成并撤销，临时安装器根信任按准确证书字节清除，公共事务不存在，应用、内核、看门狗与安装器均退出。当前包仅注册给原测试账户，服务 Auto/Running、PID 33884；原账户数据和备份保留，两个账户代理保持。产品仍使用内部签名，未作为正式发布包。

代码候选 `85f1a36` 的四份原始 CI 报告仍为 6,634 项全部通过；上一份资料提交 `fa111e1` 的[CI 38112621268](https://github.com/Water-Run/ClashSharp/actions/runs/38112621268)也已完成且成功。本轮没有重复执行已通过的完整测试集。

后续继续维护故障与重启恢复、网络/数据失败组合、页面/语言/DPI/多屏、远程 Windows 11 和发行来源矩阵；本轮通过不关闭这些范围，开发目标继续进行。

随后同一候选已补充[终态首次写入失败与跨会话继续](2026-10-11-terminal-write-recovery.md)，28 项独立核对通过；该阶段结束时服务 PID 为 34848，归属、用户文件和清理边界保持。上述 PID 33884 是账户往返阶段的观察点。
