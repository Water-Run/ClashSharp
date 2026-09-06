# Installer 与服务的共享目录权限

M4n 修复了两个生产组件之间的权限冲突。Installer 创建的 ProgramData/ClashSharp 和 MihomoService 目录授予 SYSTEM/Administrators FullControl，并向准确目标账户授予 ReadAndExecute/Synchronize；目录 owner 为 Administrators，DACL 受保护。此前服务准备运行目录时，会把这两个共享根改为 LocalSystem owner、仅 SYSTEM/Administrators 可访问，导致 association 对目标用户不可读，并与 Installer 的后续目录验证冲突。

服务现在只读检查这两级共享根，并固定从盘符到 ProgramData 的完整普通目录链。已存在的共享根必须符合相同的准确目标账户权限，祖先必须满足防替换规则；缺失、重解析、陌生账户、宽松权限或共享冲突均拒绝。服务不会创建共享根或重写其 ACL，只在已持有共享目录租约时准备原有的私有 endpoint/runtime 后代。句柄保持到同步准备结束，异常时按相反顺序释放。

目录权限策略、观察模型和 M4m 的原生只读句柄移至 Shared/Windows。Installer 和 service 通过显式 Compile Link 使用同一份源码，保持独立部署，不引入彼此的执行程序集依赖；Installer 的协议诊断仍由自己的 adapter 转换。共享策略没有 WinUI、候选、服务控制或安装执行能力，原生句柄仍只请求读取权限并拒绝共享删除。

新增 21 项服务边界用例，覆盖纯构造、完整链租约、缺失、不同 owner、旧版私有共享根、宽松/继承/拒绝/object ACE、重解析、共享失败、祖先不可信及获取后的权限变化。现有重解析测试更新为准确的服务信任错误类型。完整主程序 2330 项、Windows 安全子集 474 项通过，均 0 跳过；18 项目 Release x64 构建零警告、零错误，format 1299 个文件、零处变更、无工作区警告。

远端由管理员使用生产 Installer 目录策略和 v2 store 准备隔离 ProgramData，再通过一次性隐藏任务以真实 LocalSystem 身份调用生产服务的 PrepareRuntimeDirectory，共通过 38 项断言。首次准备及重放均保留共享目录原 ACL 字节，以及 association、普通 Prepared 日志和私有证据；新 endpoint/runtime 为 LocalSystem 私有目录。未知账户、旧版私有共享根和缺失共享目录均拒绝，故障对象保持，缺失目录未被创建。

探针的主动 ACL 故障注入及还原会增加 Windows AUTO_INHERITED 描述符标记。最终检查使用生产 guard 验证被注入两级目录的准确 owner/DACL，并对未注入目录和全部证据文件保持字节比较；生产服务准备前后的共享 ACL 仍执行完整字节比较。初次探针只因把故障注入的标记变化计作生产改变而失败，修正探针后在新隔离目录重新完整执行。任务结束已移除。

收据为 artifacts/verification/shared-roots-system-validation-m4n.json；Windows 程序集 SHA-256 为 1f52c6687fe65dbd1b1d550e018ddfc3bfd98dfcaaa84ebed84563350b8ee5af，service 为 802baaeaba6f1645f53891a8b52a5a3beb97da0194bb3d7680664026f0c2efde。测试没有启动 SCM 服务或 mihomo、修改代理、安装包或证书，不能代替签名候选安装和实际桌面验收。
