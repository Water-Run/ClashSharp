# 设置维护操作验收（2026-09-28）

在指定蓝色服务器 `WIN-07OBU2PV8C6` 的验收账户上，通过真实桌面操作检查“还原所有设置”和“清除所有数据”。开发机仅用于构建、隔离测试和整理证据。

## 已确认的问题与修改

e94a4bf 的“清除所有数据”连续显示三次确认，第二次说明“再次确认后立即执行”，实际还会出现第三次；“还原所有设置”也有两次内容重复的确认。4c91c0d 将两项操作各收敛为一次原生 `ContentDialog`，清楚说明保留或删除的数据，清理确认按钮写明“清除并重启”。保留默认取消、Esc 取消及原有操作许可和生命周期控制。8 种语言同步修改，移除未使用的重复警告文案。

此处采用 [Microsoft ContentDialog 指南](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs) 的原生按钮、明确动作及安全退出方式，并按[确认框指南](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm) 减少重复确认。设置页的还原、清理入口保持蓝色文本链接。

本地 Release x64 构建成功，零警告、零错误（32.67 秒）；修改文件格式检查通过；主程序 **3,332/3,332** 测试通过，零跳过（71 秒）。报告位于忽略目录 `artifacts/verification/server-acceptance-20260922/maintenance-regression/maintenance-confirmation-main.trx`。

## e94a4bf 原生基线

操作前核对 `closed-state-20260928-1347` 的全部 44 个备份文件散列。14:07–14:08 UTC 分别在第一层按 Enter、第二层按 Esc、第三层点击取消，均返回设置页。随后核对 36 个稳定数据文件的 SHA-256 全部未变、进程 PID 未变；活动 SQLite、进程锁及看门狗续租文件不属于这项散列比较。

14:09:52 UTC 最终确认清理，应用自动重启。配置目录、订阅及旧运行代际文件已移除；连接、流量快照、配置/节点流量、节点健康、规则命中及触发器业务表为空，两个数据库 `quick_check=ok`。主控总流量为 0，界面语言恢复自动检测、主题恢复跟随系统。重启后仅出现默认配置及新运行文件。

清理尚不能视为完整通过：日志记录了 **4 条 Maintenance 警告**，分别为仍由本进程占用的日志 SQLite 数据库、WAL、SHM 和 `RecoveryWatchdog.lock` 无法删除。旧日志内容确已清空，但物理删除的收尾与失败反馈仍须修复，不能把自动重启等同于删除完整成功。

14:16 UTC 正常退出，核对应用/内核/TUN 均未运行、系统代理关闭、期望与实际网络模式均为 Disabled。清理后状态另存为 `cleared-state-20260928-1416`，13 文件、329,515 字节，保留原始备份用于后续恢复。随后通过安装器正常卸载；14:20 UTC 包和服务均已移除。点击卸载取消时已到完成页，因此本轮不能证明中途取消行为。

原始证据保留在服务器 `C:\ClashSharpValidation\acceptance-20260922-e94a4bf`：`clear-before-cancellation.json`、`clear-cancellation-verified.json`、`clear-post-restart-inventory.json`、`clear-database-inventory.json`、`clear-log-verification.json`、`startup-after-clear-e94a4bf.json`、`post-clear-closed.json` 和两份独立状态备份。

## 4c91c0d 原生复验

14:26 UTC 服务器完成候选构建，14:28 UTC 经安装器正常安装。安装目录与 MSIX 载荷的主程序集 SHA-256 一致；安装后安装器、MSIX 和 Windows App Runtime 三项签名均为 Valid，使用验收证书，不能据此声称正式发行签名已经就绪。随后手动恢复前述 44 文件验收夹具；这不是自动升级迁移测试。

中文浅色和德语深色均检查普通窗口及最小 800×600 窗口。两种确认框的标题、正文和按钮均完整显示；“清除并重启”的德语长按钮文字也未截断。Enter 默认取消清理、Esc 取消还原及清理，取消后返回原操作入口，Return 可重新打开确认框。页面中的还原和清理入口继续使用原生主题文本链接。

14:35:43 UTC 在中文浅色最小窗口实际确认一次“还原所有设置”，没有第二重确认。界面语言恢复自动检测、主题恢复跟随系统、采样间隔恢复 30 秒、登录启动关闭。取消后、实际还原后，以及切换语言重启后，三次独立核对均证明 **25 个配置文件、控制器凭据和十张表中的全部原有记录未变**，包括 2,779 条原有日志、129 条连接、168 条流量快照、3 条配置流量、73 条节点流量、70 条节点健康和 125 条规则命中记录。三张触发器表原本为空，本轮未证明非空触发器任务的保留行为。

恢复中文浅色后，于 14:52 UTC 通过设置页正常退出。三次启动共 **42 个完成步骤全部成功，零 Warning/Error**；应用及内核进程、TUN 均为空，系统代理关闭，期望与实际网络模式均为 Disabled。另存 `closed-state-20260928-1452`，42 文件、1,019,209 字节，逐文件散列校验通过。候选保持安装，原始夹具备份仍保留。

[CI 36434939519](https://github.com/Water-Run/ClashSharp/actions/runs/36434939519) 的两项工作均成功。下载并核对四份 TRX：主程序 3,332、安装器核心 1,031、安装器界面 152、Windows 适配 1,126，合计 **5,641/5,641，通过且零跳过**。开发机没有运行安装器或改变产品设置。

## 证据与覆盖边界

忽略目录 `artifacts/verification/server-acceptance-20260922` 保留以下文件：

| 证据 | 内容与 SHA-256 |
| --- | --- |
| `evidence-e94a4bf-maintenance.zip` | 26 份基线 JSON，`6E9043FAEFDDD5984EFECF9B1EB4E0F8A4B55ED8EF61FD0C238A2CB09C39C611` |
| `evidence-4c91c0d-maintenance.zip` | 16 份新候选 JSON，`40A82B4A907D4BA850C76B898EF16592F259B1CAA87FCB2A8C1A8C0A34ECA057` |
| `desktop-evidence-maintenance-20260928T145235Z.tar.gz` | 87 张截图与操作记录，`F75F3BFE4D7681AC9C3E1B8E5D1F7DEDB5141393E5A83EA3D09261769145ABBA` |
| `maintenance-native-verification.json` | 载荷、签名、数据保留、启动、退出和截图完整性核对结果 |
| `ci-4c91c0d-verification.json` | 四份 TRX 的数量、结果和散列核对 |

`Verify-Maintenance-Baseline.py`、`Verify-4c91c0d-Native.py` 和 `Verify-4c91c0d-CI.py` 均执行通过；归档提取前核对 SHA-256、文件数量及安全路径。

完整功能矩阵当前为 **83 通过、38 部分、20 待测**。`settings.19` 的确认、取消及本夹具还原通过；`settings.20` 仍为部分通过。4c91c0d 没有修改底层清理实现，也没有重新执行实际清空，不能把 e94a4bf 的部分成功升级为新版本完整通过。下一步需在宿主仓库及进程资源释放后再删除文件，并明确报告部分失败；还须覆盖活动代理下的清理、失败与恢复。完整语言/主题/DPI 组合、非空触发器保留、设置代际整体切换和正式发行签名等仍未完成，本轮不宣称生产就绪。
