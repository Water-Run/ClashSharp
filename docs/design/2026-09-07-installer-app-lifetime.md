# Installer 与 App 生命周期协作

## 执行约定

进程快照不能防止检查之后的新 App 启动。普通 Installer parent 和经过认证的 helper 现在
分别持有 `%LocalAppData%\ClashSharp\InstallerMutation.lock` 的只读句柄，允许双方同时读取，
禁止写入与删除；App 的现有独占生命周期句柄与它们互斥。文件名由 InstallerStateLayout
提供，App 和 RecoveryWatchdog 仅链接该声明，不引入安装器运行程序集。

Parent 在创建执行会话之前取得锁，等待会话及 broker 收尾之后再释放。Helper 在机器范围
互斥之后、创建保护存储和读取日志之前取得自己的句柄，并在所有事务资源关闭之后释放。
因此 parent 异常退出不会释放仍在工作的 helper 的启动保护；取消也不会自动撤销已取得的
句柄。所有持锁进程退出后，Windows 关闭句柄，下一次 App 启动再检查持久日志。

应用正在运行时，安装器在创建事务存储、Prepared 或机器修改之前被拒绝。多个 helper
仍由机器范围互斥对象串行化；持久日志负责进程退出后的恢复判定。三者各有明确职责。

## 用户路径边界

目标用户目录仍来自经过验证的 SID 和 64 位 ProfileList。Parent 还核对当前 SID 与实际
LocalApplicationData 已知目录；自定义重定向与固定机器部署路径不一致时直接拒绝。
从盘符根目录到 LocalAppData 的每个现有组件都以不跟随重解析点的句柄打开，持有期间
不能重命名。Parent 仅在已锁定的普通已知目录内创建产品目录和空协调文件。

提权 helper 只能打开已有目录与文件，不能为目标用户创建文件、跟随目录重定向、修复权限
或写入协调文件内容。父进程退出后 helper 仍保有同一文件的独立句柄，不需要把 parent
提供的文件句柄或任意路径当作权限依据。初始化失败会按相反顺序关闭所有已取得的句柄。

## MSIX 文件可见性

MSIX 默认可能把新建的 AppData 文件重定向到包的私有位置，因而相同的文本路径不能证明
两个进程使用同一个协调文件。Windows 11 支持按目录排除这种重定向，且需要
unvirtualizedResources capability。[微软的文件虚拟化说明](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
与 [ExcludedDirectory schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-virtualization-excludeddirectory)
给出了这一约定。

主 MSIX 只把 `$(KnownFolder:LocalAppData)\ClashSharp` 声明为排除目录，保留其余文件与注册表
虚拟化。包内用户数据继续使用既有 LocalState 路径。源清单测试验证唯一排除路径和必要
capability；最终 MSIX 的构建及桌面文件可见性仍须由实际打包与安装验收确认。

## 验证范围

本机原生文件系统测试覆盖 App/Installer 互斥、parent/helper 同时持有、只读内容保持、
已接受租约的取消、helper 不创建缺失目录或文件、身份/路径不匹配，以及失败后所有祖先句柄
均已释放。组合测试验证 parent 会话清理前持续持锁，以及 helper 拒绝运行中 App、初始化
失败和清理异常时的资源顺序。

远端另以独立进程调用编译后的 WindowsInstallerApplicationLock.Acquire，以及 App 与
watchdog 共用的 RecoveryWatchdogFileLock.TryAcquireAsync。除目标配置目录替换为隔离
验证目录之外，文件打开和等待代码均为生产实现。验证了互斥、终止 parent 后 helper 继续
阻止启动、终止最后一个持有者后可重新启动，以及实际目录 junction 在创建文件之前被拒绝。
所有验证进程已结束，重定向目标保持为空，没有访问实际用户配置、服务、证书信任或代理设置。

脱敏收据为 artifacts/verification/application-lock-validation-m4e.json。该结果不等于包内
桌面进程、真实 SID/alternate-admin 协作或完整安装故障矩阵已经通过。跨账户换绑仍需要对
旧账户与新账户分别建立相应的生命周期保护，不能仅把 AllowReassociation 打开。
