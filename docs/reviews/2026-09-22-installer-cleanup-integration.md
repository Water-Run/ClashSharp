# 普通卸载自动空目录清理接入（2026-09-22）

在 `68f450a` 归并基线上，将此前独立保存的清理模块接入普通安装/修复/卸载的生产装配。所有变更直接进入唯一开发分支 `main`。这批实现不等于完整 ClashSharp 或正式 1.0.0 已完成。

## 生产行为

- 创建固定的六种产品目录时，只有当前调用真正新建成功才记录卷和文件身份。旧目录不补认领。
- 受保护事务存储在清除已验证的卸载 journal 前，将完全相同的事务持久记录在待删目录之外。parent 即使观察到 active 文件或整个根目录消失，仍会读到同一卸载恢复状态；两份证据不一致则拒绝操作。
- 普通 helper 在成功 Clear 回复前先释放机器操作和持久存储的句柄，再执行固定目录清理，整个过程继续持有应用锁和机器权威锁。失败不能发送成功回复，终结状态保留供恢复。
- 每个目录重新验证身份、安全描述符、普通目录属性、子项和命名数据流。只非递归删除身份相同且为空的已记录目录；缺证据、被替换或有内容的目录保留，并在结果中报告。
- 所有目录检查完成后才删除终结账本，独立确认其缺失后回复。终结器将仍缺失的目录按 Missing 处理，不制造新事务身份。只读 parent 不创建目录；提权 helper 恢复时的受保护存储可能重建工作目录，其新的创建身份继续记入同一账本，并在本次终结时重新清理。
- 换绑的普通事务检查及准备存储也读取终结状态，不能越过尚未完成的卸载。旧账户卸载继续保留当前账户可能使用的共享目录，不调用全局清理器。

协议、固定路径和崩溃边界见[设计](../design/2026-09-12-installer-empty-directory-cleanup.md)。

## 验证与复现

本机 SDK `10.0.201`：18 项目 Release x64 构建零警告、零错误；format 验证 1544 文件、零变更、无加载警告。四组测试共 **5199 / 5199**，零失败、零跳过：主程序 2938、Installer Core 1022、Presentation 143、Windows 1096。

新增 21 项本机可运行测试，覆盖真正的 protected store、authority/session/framing 与 finalizer 装配、terminal-only parent 读取、冲突拒绝、资源释放、两把锁的持续持有、清理失败无成功 frame、取消与部分清理恢复。首次运行暴露了测试 fixture 使用安装相位顺序初始化卸载的问题；修正为实际卸载相位后，45 项相关定向测试全部通过。

另有 **7 项新增原生测试**须在隔离的管理员 Windows CI 全量执行；它们使用唯一的 `ProgramData/ClashSharp.DirectoryCleanup.Tests.<nonce>` 测试根，验证实际创建身份、受保护账本、句柄删除、既有/替换目录保留、普通子文件/目录数据流保留和外来账本拒绝。恢复测试在全部目录已删但账本尚存的切点注入中断，验证真实 parent 不创建目录，fresh protected store 重建工作目录后仍保留原事务且不重建 active 文件，最后继续清理无残留。测试不操作真实产品路径，收尾仅删除确切创建的文件和空目录，不递归删除未知内容。此提交中的本机验证数字不包含这 7 项，也不包含两个实际证书存储测试类的 20 项。

```powershell
dotnet build ClashSharp/ClashSharp.slnx -c Release -p:Platform=x64 --no-restore
dotnet test ClashSharp/ClashSharp.Tests/ClashSharp.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet test ClashSharp/ClashSharp.Installer.Tests/ClashSharp.Installer.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Presentation.Tests/ClashSharp.Installer.Presentation.Tests.csproj -c Release -p:Platform=AnyCPU --no-build
dotnet test ClashSharp/ClashSharp.Installer.Windows.Tests/ClashSharp.Installer.Windows.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName!~WindowsCurrentUserCertificateStoreAdapterTests&FullyQualifiedName!~WindowsTargetUserCertificateStoreAdapterTests&FullyQualifiedName!~WindowsInstallerDirectoryCleanupNativeTests'
```

CI 执行不带上述过滤器的 Windows 测试，并保留四份 TRX 和完整离线开发包。每次提交的实际结果以绑定该 SHA 的 [CI](https://github.com/Water-Run/ClashSharp/actions/workflows/ci.yml) 为准；本机日志和 TRX 位于忽略目录 `artifacts/verification/directory-cleanup-*`。

接入提交 `996ba3c` 的[首轮 CI](https://github.com/Water-Run/ClashSharp/actions/runs/35730115081) 构建和完整开发包成功，但原生测试中 5 项在账本替换时失败；只有拒绝外来账本的用例通过。独立普通权限临时文件探针复现 Windows 行为：即使旧目标 handle 允许 delete sharing，`MoveFileEx` 替换仍报 Win32 5；关闭该 handle 后替换成功。修复将旧文件 handle 保留至最后一次身份/字节核对，随后关闭它再执行原子替换；机器权威锁、完整父路径 pin、保护 ACL 和首次发布禁止覆盖的约束保持。该失败保留为回归证据，后续必须由相同原生用例复验。

## 剩余验收

同一新候选仍须完成真实安装、修复、卸载与清理中断恢复矩阵；本批源码/测试接入不借用旧候选的实机证据。旧账户最后退出后的共享目录处理需单独验收。创建完成但身份未持久提交的断点只能保留目录；账本临时文件提交前进程崩溃不按名称认领残留；最后账本已删但成功回复丢失时保留不确定状态。这些边界没有承诺任意崩溃都零残留。

下一步继续设置代际的整体生产切换、完整页面/正常退出/RTL 验收及正式签名发行流程，范围见[开发状态](2026-09-22-development-status.md#后续实现顺序)与[设置设计](../design/2026-09-08-settings-generation-cutover.md)。
