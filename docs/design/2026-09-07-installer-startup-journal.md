# Installer 日志与主程序启动边界

## 修复的问题

C# Installer 的持久日志位于
`%ProgramData%\ClashSharp\Installer\v2\transaction-v2.json`，但主程序的启动读取器
此前只观察旧实现的 `Installer\transaction.json`。即使新日志已经持久化，主程序也可能
把当前状态判断为 Clear，随后启动恢复看护与网络接管。各自通过的单元测试没有覆盖两端的衔接。

现在由 `InstallerStateLayout` 声明固定的路径组成，持久存储与 Windows 根目录保护器使用
同一约定。主程序只链接这份常量源文件，不引用 Installer Core 或 Windows 的运行程序集，
因此不会取得安装、服务或日志写入能力。

## 启动判定

主程序先取得现有的进程生命周期锁，然后检查 Windows CommonApplicationData 下的固定目录：

1. 旧版标记存在时仍阻止启动；新实现不会解释、迁移或清除旧版事务。
2. 旧版标记不存在时继续检查 v2 目录和当前日志。
3. 任意普通、可读日志均为 Pending，包括空文件、损坏内容及尚未清理的 Verified 阶段。
4. 目录冲突、重解析点、无法读取等不明确状态为 Invalid；相对测试根目录也被拒绝。
5. 仅在两个日志都不存在时返回 Clear；不创建目录、修复权限或修改任何文件。

读取只确认文件存在，允许 Installer 的原子替换继续进行，不以读取到的 JSON 字段决定
是否开放权限。Pending 和 Invalid 都沿用终态启动阻断：不启用恢复看护，不进入 Core、
Service 或系统代理/TUN 接管，关闭诊断窗口也不会调用网络关闭流程。

Verified 日志的删除仍属于提权事务 authority。主程序必须等删除完成后才能正常启动，
不能把 Verified 字段等同于安装器已经结束全部收尾工作。

## 验证与限制

新增跨组件用例直接调用生产 `FileInstallerTransactionStore`，在临时目录分别执行 Install、
Repair、Uninstall 的完整持久阶段。每个阶段都创建新的主程序读取器和启动 gate，验证其
关闭 mutation admission，并重新载入原日志确认主程序未改动它。只有生产存储的
ClearVerifiedAsync 成功后，新启动才恢复开放。测试仅替换机器目录权限 guard，完全不接触
实际 ProgramData、服务、MSIX 注册、证书信任或网络设置。

新旧标记的文件类型、独占打开、符号链接及空目录边界另有文件系统测试；已有架构测试继续
检查主程序的先锁定、后观察、再启用看护的顺序，以及没有 Installer 运行程序集依赖。

本节点修复已经存在的日志无法被主程序观察的问题。新 Installer 对主程序生命周期锁的持有
仍须接入，才能覆盖“检查进程后、Prepared 持久化前，另一个主程序开始启动”的并发窗口；
机器范围 helper 互斥只保护多个 helper，不能代替这项 App/Installer 协作。跨账户换绑和
正式签名候选的实际安装、故障恢复及桌面验收仍单独推进。
