# Mihomo 重载与运行缓存所有者

已安装的 `c0495f4` 服务在断网 Windows Sandbox 中能够启动真实 Mihomo，返回
Running、代际 1 与正确配置摘要。生产 IPC 客户端在发送凭据前验证实际管道的
SCM 进程；随后查询确认 TUN 开启、Rule 模式、mixed-port 为 0，TCP 9090 和
10000 未监听，代理组查询成功。测试使用专门的最小配置，没有用户配置输入。

同一服务收到代际 2 的 Global 配置后返回 `service.child.staging_failed`，仍保留
代际 1 的核心进程；停止命令成功。这不是完整运行矩阵通过。

## 原因与修复

诊断运行 `ee7dc174ffd14c4d8680ab1cd90b39e8` 记录了服务错误类型
`UnauthorizedAccessException`。实际核心创建的 `cache.db` 及一个运行子目录的
所有者是 `S-1-5-32-544`（内置 Administrators），权限继承自私有父目录，仅授予
SYSTEM 和 Administrators 完全控制。服务创建的 effective 目录和配置仍由
`S-1-5-18`（SYSTEM）持有。

旧的运行树检查要求每个后代都由 SYSTEM 持有，因此将这个真实核心缓存误判为
外来文件，无法准备下一代配置。修复把描述符检查提取为单独的只读策略：

- 运行根目录继续要求 SYSTEM 所有者及受保护 DACL。
- 后代可由 SYSTEM 或内置 Administrators 持有；这两个主体本来就拥有私有父
  目录的完全控制权。检查不接纳其他所有者，也不修改活跃缓存的所有权。
- 根和后代的所有访问条目仍只允许这两个主体；SYSTEM 必须对当前对象拥有
  完全控制。仅对子项继承的授权不能代替当前对象权限，对 SYSTEM 的拒绝条目
  会导致验证失败。
- 既有重解析点拒绝、目录枚举上限与共享根保护保持。

这项修复只调整运行子项的合法所有者判断，不能扩大为 Installer 的根目录、
证书、关联或跨账户权限策略。

## 验证与范围

新增 11 项内存描述符回归，覆盖实际缓存的文件与目录权限、SYSTEM 根与子项、
管理员根拒绝、普通用户所有者、外来授权、缺少 SYSTEM 完全控制、拒绝条目及
仅可继承的授权。成功和失败路径均确认描述符字节未变，没有在开发机修改 ACL。

完整 Release x64 构建 0 警告、0 错误；主程序 2368 项测试通过、0 失败、0 跳过。
完整 format 加载 1402 个文件、0 处变更。对应本地证据为
`1.0.0-m5e-main.trx` 及 `artifacts/verification` 中的 M5e 构建和格式日志。

旧候选的首次夹具运行因新增探针未进入额外输入清单，在安装前失败；修正后两次
运行复现上述重载错误。失败路径的兜底 Remove-AppxPackage 也报告错误，不能将
客体清理计为通过；每个 Sandbox 最终均由主机销毁，输入与开发机代理未变。

`f1fca8e` 的构建测试与离线打包 [CI 均成功](https://github.com/Water-Run/ClashSharp/actions/runs/34184314054)。
下载四份 TRX 核对共 4433 项通过、0 失败、0 跳过，包含全部 11 项新回归。
`ci-validation-m5e.json` 和 `ci-runtime-security-regressions-m5e.json` 保存实际结果。

## 修复候选的 Windows 11 重放

使用同一提交的 CI MSIX 和生产 parent/helper 编译测试入口，签名和信任仅在断网
Sandbox 中进行。候选 MSIX SHA-256 为
`7d57b41bb13882eb3ab356f17ad7727675501d24590a1085d62547673c40c411`，
签名入口为 `cc4cbf12f4ccc82e7996772017d31f4d4ec0b02d03dd855827c0f0303740d02d`。
两项运行都采用客体卷根 ACL 夹具并在结束时恢复，没有放宽生产安装目录策略。

运行 `f12fac3fab7c4bb5ad43cf6c1c0648e8` 完成安装后的八步真实 IPC 流程：Hello、
Start、有效配置查询、代理组查询、Reload、新配置查询、旧代际查询拒绝、Stop。
实际核心从 PID 8164 切换至 7420；持有的旧进程句柄确认退出，新配置为代际 2、
Global 模式，源文件摘要保持，TUN 开启，mixed-port 为 0，TCP 9090 和 10000
未监听。重载前的 cache.db 仍由 Administrators 持有，证明修复接纳了实际缓存。

随后生产卸载进程 7320 返回 `installer.completed`、Verified，无待恢复事务。
在兜底清理之前，包、服务、机器与用户证书、机器载荷及服务运行数据均不存在。
九项客体清理通过，没有服务宿主崩溃事件；Sandbox 已销毁、输入与开发机代理未变。
收据为 `artifacts/verification/installer-service-runtime-m5e.json`。

## 核心退出恢复与运行中卸载

同一候选的运行 `4d7bc20fe93f4512b599d5fd82ce1cf9` 使用最小配置验证实际核心退出：

- 启动代际 1 的 Rule 配置后，仅将源文件改成 Global，不发送新配置命令。
  三次终止准确路径且持有句柄的自有核心后，服务分别创建新进程，继续使用原来的
  代际 1、摘要和 Rule 模式；未提交的源文件保持原样。
- 第四次退出后返回 Faulted、无子进程及 `service.child.restart_exhausted`。
  绑定查询返回 `service.controller.not_ready`，没有有效配置；超过最长重试间隔
  后仍未启动新核心。
- 新的显式 Start 接受代际 2，Global 与 TUN 状态通过实际控制查询。
  探针成功路径未发送 Stop，将 PID 8140 保持运行交给卸载流程。
- 生产卸载进程 6496 完成服务停止和全部产品清理；卸载前持有的核心句柄确认该
  进程已退出。产品缺席快照仍先于兜底清理，九项客体清理均通过；服务宿主未崩溃。

收据为 `artifacts/verification/installer-service-recovery-m5e.json`。前一轮夹具
`82c624f3f4224e0d92dfc1b52c8059a5` 已通过核心恢复，但读取活跃 cache.db 摘要时
遇到共享冲突，没有执行生产卸载，不能计为全流程通过。其兜底清理另记录到框架
依赖移除返回 0x80073CF1；修正夹具仅在精确依赖的当前用户注册经复查确实缺席后
接纳这个结果，其他错误仍失败。最终成功运行未触发该补偿分支。

这些运行使用生产服务和引擎，入口是受客体身份限制的原生 API 探针；不包含 WPF
操作、普通用户/UAC 交互、服务宿主强制崩溃、主机断电或实际外网 TUN 转发验收。
