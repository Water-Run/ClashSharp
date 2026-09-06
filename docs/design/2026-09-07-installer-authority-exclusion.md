# Installer 跨进程执行边界

固定日志的读取、比较和原子替换需要由同一个独占执行范围保护。FileInstallerTransactionStore
的 SemaphoreSlim 只覆盖一个对象实例；单独依靠原子替换不能防止两个提权助手读取同一代
日志后分别执行机器修改、再覆盖对方的后继状态。

WindowsMachineHelperAuthorityFactory 在创建受保护存储和读取恢复日志前取得
Global\ClashSharp.Installer.Authority.v1 互斥对象。该名称不含账户、会话、事务或产品版本号，
所以不同桌面会话、不同用户的已认证提权助手共享同一个执行边界。版本后缀标识锁协议；
产品升级不得更换该名称而绕过旧版本仍持有的锁。

对象 owner 固定为 Builtin Administrators，受保护 DACL 仅包含 SYSTEM 和
Builtin Administrators 的完整访问权限。新建时直接提供该描述符；已有对象在已打开的
句柄上重新读取并验证，不修补不符的权限，不把名称存在视为可信。当前被占用时立即
拒绝第二个助手，尚未创建事务存储，也未调用包、证书或服务执行器。

一个专用线程负责获取和释放互斥对象，异步会话只持有可等待释放的租约。会话资源先停止、
关闭，随后才释放互斥对象。取消已接受的会话不会提前释放锁；初始化失败和资源清理异常
也会关闭租约。Windows 要求互斥对象由持有线程释放，持有线程或进程异常退出时，后续
等待者可以取得 abandoned 对象；这些行为见 [Microsoft Mutex 文档](https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex?view=net-10.0)。

重新取得 abandoned 对象后，助手仍从受保护存储读取、验证原日志，再由已有状态机决定
恢复动作。锁可重新获取不代表旧事务已经完成，不会删除日志或跳过恢复校验。创建、权限
和已有对象的行为见 [CreateMutexExW](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createmutexexw)。

本机测试使用注入适配器和随机 Local 命名对象，实际调用生产 Win32 wait/release 代码，
不打开固定生产对象。远端验证直接调用编译后的默认锁实现：两个独立进程互斥、正常
释放后重入、终止持锁进程后重入均通过，并验证了默认对象的实际权限。该探针没有创建
安装事务、部署包、写证书信任或服务，也没有修改代理。

跨账户换绑将在同一锁范围内执行，不能另外建立按账户区分的并行写入通道。旧、新账户的
持久阶段、ACL 转移、关联替换和 UI 明确确认仍需完整实现与实机验证；本边界只关闭
并发写入缺口，不代表换绑或生产安装已经完成。
